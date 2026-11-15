using System.Text;
using System.Text.RegularExpressions;
using jd.resolver.workload;

namespace jd.resolver.release;

/// <summary>
/// Turns a release manifest into a <see cref="ReleaseSet"/>. Everything about a workload (name, team, image) is derived
/// from its definition text by <see cref="ComposeAsync"/>, which loading a release set runs too, so a set is only ever
/// built from definitions that passed every rule. Every problem is collected so a manifest can be fixed in one pass.
/// </summary>
public static class ReleaseBuilder
{
    // A tag can be moved to another image after the release is cut; only a digest pins the content.
    private static readonly Regex PinnedByDigest = new("@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    // Strict, so a byte sequence that is not UTF-8 is an error and not a silent replacement character.
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Builds the set <paramref name="manifest"/> describes, from the definitions it names, each read through <paramref name="definitions"/>.</summary>
    public static async Task<ReleaseSetResult> BuildAsync(SourceDocument manifest, DocumentReader definitions, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var index = await SchemaIndex.Shipped;
        // The manifest's kind: the one kind that names the definitions by path.
        var loaded = await DocumentLoader.LoadAsync(manifest.Name, manifest.Text(), [index.KindsBinding(Roles.ReleaseDefinitionPath).Single()], cancellationToken);
        if (loaded.Value is not { } document)
        {
            return new ReleaseSetResult(null, loaded.Errors);
        }

        var release = new Binder(index).ReadRelease(document);
        var errors = new List<LoadError>();
        var sources = new List<WorkloadSource>();
        foreach (var entry in release.Workloads)
        {
            var (name, content) = await definitions(entry.DefinitionPath, cancellationToken);
            if (content is not { } bytes)
            {
                errors.Add(new LoadError(manifest.Name, entry.DefinitionLocation, $"workload definition '{name}' not found."));
                continue;
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(bytes.Span);
            }
            catch (DecoderFallbackException)
            {
                errors.Add(new LoadError(name, string.Empty, "not valid UTF-8."));
                continue;
            }

            sources.Add(new WorkloadSource(name, string.Empty, ReleaseHash.Of(bytes.Span), text, entry));
        }

        return errors.Count > 0
            ? new ReleaseSetResult(null, errors)
            : await ComposeAsync(manifest.Name, release, createdAt, sources, cancellationToken);
    }

    /// <summary>
    /// The one place a release set is made: parses every definition, derives name, team and image, and applies the rules
    /// (valid definition, image pinned by digest, unique names, known dependencies, no cycle). <paramref name="file"/>
    /// names the manifest or release set <paramref name="release"/> was read from, in errors about names and dependencies, located where it holds them.
    /// </summary>
    internal static async Task<ReleaseSetResult> ComposeAsync(string file, ReleaseDocument release, DateTimeOffset createdAt, IReadOnlyList<WorkloadSource> sources, CancellationToken cancellationToken)
    {
        var errors = new List<LoadError>();
        var workloads = new List<ReleaseWorkload>();
        foreach (var source in sources)
        {
            // The YAML parser does not take a byte order mark; the SHA-256 was taken over the bytes with it.
            var loaded = await WorkloadFile.ParseAsync(source.File, source.Definition.TrimStart('\uFEFF'), cancellationToken);
            if (loaded.Value is not { } workload)
            {
                errors.AddRange(loaded.Errors.Select(e => e with { Location = Within(source.Location, e.Location) }));
                continue;
            }

            var image = workload.Image;
            if (!PinnedByDigest.IsMatch(image))
            {
                errors.Add(new LoadError(source.File, Within(source.Location, workload.ImageLocation), $"'{image}' is not pinned by digest; a tag can change under a release. Use <image>@sha256:<64 hex digits>."));
                continue;
            }

            workloads.Add(new ReleaseWorkload(workload.Name, workload.Team, source.Sha256, source.Definition, image, source.Entry.DependsOn));
        }

        // Names and dependencies are only meaningful once every definition loaded; until then they would report false errors.
        if (errors.Count == 0)
        {
            errors.AddRange(CheckNames(file, workloads, sources));
        }

        if (errors.Count > 0)
        {
            return new ReleaseSetResult(null, errors);
        }

        var (order, cycle) = TopologicalOrder.Sort(workloads.ToDictionary(w => w.Name, w => (IReadOnlyCollection<string>)w.DependsOn));
        if (cycle is not null)
        {
            return new ReleaseSetResult(null, [new LoadError(file, release.WorkloadsLocation, $"dependency cycle: {string.Join(" -> ", cycle)} (each depends on the next).")]);
        }

        var seconds = createdAt.ToUniversalTime();
        return new ReleaseSetResult(new ReleaseSet(release.Label, seconds.AddTicks(-seconds.Ticks % TimeSpan.TicksPerSecond), workloads, order), []);
    }

    // A location in a definition, within where the definition sits in its file (WorkloadSource.Location); either may be empty.
    private static string Within(string definition, string location) =>
        definition.Length == 0 || location.Length == 0 ? definition + location : $"{definition}.{location}";

    // Every source made a workload (else the names are not checked), so the two lists are in step.
    private static IEnumerable<LoadError> CheckNames(string file, List<ReleaseWorkload> workloads, IReadOnlyList<WorkloadSource> sources)
    {
        for (var i = 0; i < workloads.Count; i++)
        {
            var (workload, entry) = (workloads[i], sources[i].Entry);
            var first = workloads.FindIndex(w => w.Name == workload.Name);
            if (first != i)
            {
                yield return new LoadError(file, entry.DefinitionLocation, $"workload name '{workload.Name}' is already used by {sources[first].Entry.Location}; names must be unique within a release.");
            }

            for (var j = 0; j < workload.DependsOn.Count; j++)
            {
                var dependency = workload.DependsOn[j];
                if (dependency == workload.Name)
                {
                    yield return new LoadError(file, entry.DependsOnLocations[j], $"'{dependency}' cannot depend on itself.");
                }
                else if (workloads.All(w => w.Name != dependency))
                {
                    yield return new LoadError(file, entry.DependsOnLocations[j], $"'{dependency}' is not a workload in this release.");
                }
            }
        }
    }
}
