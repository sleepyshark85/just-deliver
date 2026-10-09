using System.Text;
using System.Text.RegularExpressions;
using jd.resolver.workload;

namespace jd.resolver.release;

/// <summary>
/// One workload going into a release set.
/// </summary>
/// <param name="File">Names the definition in its errors: the definition file when creating, the release set when loading.</param>
/// <param name="Location">Prefix for locations inside the definition: empty for a definition file, <c>workloads[i].definition</c> in a release set.</param>
/// <param name="Sha256">Lowercase hex SHA-256 over the exact bytes of the definition file.</param>
/// <param name="Definition">The definition text as decoded from those bytes (a byte order mark is kept, so it re-encodes to them).</param>
/// <param name="DependsOn">Names of workloads that deploy first.</param>
internal sealed record WorkloadSource(string File, string Location, string Sha256, string Definition, IReadOnlyList<string> DependsOn);

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

    public static async Task<ReleaseSetResult> BuildAsync(string manifestPath, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            return new ReleaseSetResult(null, [new LoadError(manifestPath, string.Empty, "release manifest not found.")]);
        }

        var (manifest, parseErrors) = await ReleaseDocument.ParseAsync(manifestPath, await File.ReadAllTextAsync(manifestPath, cancellationToken), ReleaseDocument.ManifestSchema, cancellationToken);
        if (manifest is null)
        {
            return new ReleaseSetResult(null, parseErrors);
        }

        // A manifest path has a file name and so a directory; the schema guarantees the shapes read below.
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var errors = new List<LoadError>();
        var sources = new List<WorkloadSource>();
        var entries = manifest["workloads"]!;
        for (var i = 0; i < entries.Count(); i++)
        {
            var path = Path.Combine(directory, (string)entries[i]!["definition"]!);
            if (!File.Exists(path))
            {
                errors.Add(new LoadError(manifestPath, $"workloads[{i}].definition", $"workload definition '{path}' not found."));
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            string text;
            try
            {
                text = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                errors.Add(new LoadError(path, string.Empty, "not valid UTF-8."));
                continue;
            }

            var dependsOn = entries[i]!["dependsOn"]?.Select(t => (string)t!).ToList() ?? [];
            sources.Add(new WorkloadSource(path, string.Empty, ReleaseDocument.Sha256(bytes), text, dependsOn));
        }

        return errors.Count > 0
            ? new ReleaseSetResult(null, errors)
            : await ComposeAsync(manifestPath, (string)manifest["label"]!, createdAt, sources, cancellationToken);
    }

    /// <summary>
    /// The one place a release set is made: parses every definition, derives name, team and image, and applies the rules
    /// (valid definition, image pinned by digest, unique names, known dependencies, no cycle). <paramref name="file"/>
    /// names the manifest or release set in errors about names and dependencies.
    /// </summary>
    internal static async Task<ReleaseSetResult> ComposeAsync(string file, string label, DateTimeOffset createdAt, IReadOnlyList<WorkloadSource> sources, CancellationToken cancellationToken)
    {
        var errors = new List<LoadError>();
        var workloads = new List<ReleaseWorkload>();
        foreach (var source in sources)
        {
            // The YAML parser does not take a byte order mark; the SHA-256 was taken over the bytes with it.
            var loaded = await WorkloadFile.ParseAsync(source.File, source.Definition.TrimStart('﻿'), cancellationToken);
            if (loaded.Workload is not { } workload)
            {
                errors.AddRange(loaded.Errors.Select(e => source.Location.Length == 0 ? e : e with { Location = e.Location.Length == 0 ? source.Location : $"{source.Location}.{e.Location}" }));
                continue;
            }

            // A valid definition has these (the workload schema requires them).
            var image = (string)workload["container"]!["image"]!;
            if (!PinnedByDigest.IsMatch(image))
            {
                var location = source.Location.Length == 0 ? "container.image" : $"{source.Location}.container.image";
                errors.Add(new LoadError(source.File, location, $"'{image}' is not pinned by digest; a tag can change under a release. Use <image>@sha256:<64 hex digits>."));
                continue;
            }

            workloads.Add(new ReleaseWorkload((string)workload["metadata"]!["name"]!, (string)workload["metadata"]!["team"]!, source.Sha256, source.Definition, image, source.DependsOn));
        }

        // Names and dependencies are only meaningful once every definition loaded; until then they would report false errors.
        if (errors.Count == 0)
        {
            errors.AddRange(CheckNames(file, workloads));
        }

        if (errors.Count > 0)
        {
            return new ReleaseSetResult(null, errors);
        }

        var (order, cycle) = TopologicalOrder.Sort(workloads.ToDictionary(w => w.Name, w => (IReadOnlyCollection<string>)w.DependsOn));
        if (cycle is not null)
        {
            return new ReleaseSetResult(null, [new LoadError(file, "workloads", $"dependency cycle: {string.Join(" -> ", cycle)} (each depends on the next).")]);
        }

        var seconds = createdAt.ToUniversalTime();
        return new ReleaseSetResult(new ReleaseSet(label, seconds.AddTicks(-seconds.Ticks % TimeSpan.TicksPerSecond), workloads, order), []);
    }

    private static IEnumerable<LoadError> CheckNames(string file, List<ReleaseWorkload> workloads)
    {
        for (var i = 0; i < workloads.Count; i++)
        {
            var workload = workloads[i];
            var first = workloads.FindIndex(w => w.Name == workload.Name);
            if (first != i)
            {
                yield return new LoadError(file, $"workloads[{i}].definition", $"workload name '{workload.Name}' is already used by workloads[{first}]; names must be unique within a release.");
            }

            for (var j = 0; j < workload.DependsOn.Count; j++)
            {
                var dependency = workload.DependsOn[j];
                if (dependency == workload.Name)
                {
                    yield return new LoadError(file, $"workloads[{i}].dependsOn[{j}]", $"'{dependency}' cannot depend on itself.");
                }
                else if (workloads.All(w => w.Name != dependency))
                {
                    yield return new LoadError(file, $"workloads[{i}].dependsOn[{j}]", $"'{dependency}' is not a workload in this release.");
                }
            }
        }
    }
}
