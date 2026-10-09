using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace jd.resolver.release;

/// <summary>Writes a <see cref="ReleaseSet"/> once and reads it back, re-verifying every embedded definition against its recorded hash.</summary>
public static class ReleaseSetFile
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithQuotingNecessaryStrings()
        .WithEventEmitter(next => new MultilineEmitter(next))
        .Build();

    // Multi-line definitions are written as literal blocks, so they stay readable in the set. A block scalar would turn
    // CRLF into LF and cannot always tell where leading white space ends and the indentation starts, which would change
    // the bytes the hash covers, so such text is quoted and escaped instead.
    private sealed class MultilineEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Value is string text && text.Contains('\n'))
            {
                eventInfo.Style = text.Contains('\r') || char.IsWhiteSpace(text[0]) || text[0] == '\uFEFF' ? ScalarStyle.DoubleQuoted : ScalarStyle.Literal;
            }

            base.Emit(eventInfo, emitter);
        }
    }

    /// <summary>Creates the file; throws <see cref="IOException"/> if it exists, because a release set is never changed.</summary>
    public static async Task WriteAsync(ReleaseSet set, string path, CancellationToken cancellationToken = default)
    {
        var document = new Dictionary<string, object>
        {
            ["kind"] = "ReleaseSet",
            ["label"] = set.Label,
            ["createdAt"] = set.CreatedAt.ToString(TimeFormat, CultureInfo.InvariantCulture),
            ["workloads"] = set.Workloads.Select(w => new Dictionary<string, object>
            {
                ["definitionSha256"] = w.DefinitionSha256,
                ["dependsOn"] = w.DependsOn,
                ["definition"] = w.Definition,
            }).ToList(),
        };

        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(Serializer.Serialize(document).AsMemory(), cancellationToken);
    }

    public static async Task<ReleaseSetResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new ReleaseSetResult(null, [new LoadError(path, string.Empty, "release set not found.")]);
        }

        var (body, parseErrors) = await ReleaseDocument.ParseAsync(path, await File.ReadAllTextAsync(path, cancellationToken), ReleaseDocument.SetSchema, cancellationToken);
        if (body is null)
        {
            return new ReleaseSetResult(null, parseErrors);
        }

        // The schema guarantees the shapes read here.
        if (!DateTimeOffset.TryParseExact((string)body["createdAt"]!, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAt))
        {
            return new ReleaseSetResult(null, [new LoadError(path, "createdAt", "not a valid UTC time.")]);
        }

        // Only the definitions and what pins them are stored. Everything else (name, team, image, order) is derived from
        // the definitions by the same code that created the set, after each definition is checked against its hash.
        var errors = new List<LoadError>();
        var sources = new List<WorkloadSource>();
        foreach (var (item, i) in body["workloads"]!.Select((item, i) => (item, i)))
        {
            var definition = (string)item["definition"]!;
            var recorded = (string)item["definitionSha256"]!;
            var actual = ReleaseDocument.Sha256(Encoding.UTF8.GetBytes(definition));
            if (actual != recorded)
            {
                errors.Add(new LoadError(path, $"workloads[{i}].definition", $"does not match definitionSha256 (recorded {recorded}, content hashes to {actual}); the release set was modified."));
            }

            sources.Add(new WorkloadSource(path, $"workloads[{i}].definition", recorded, definition, item["dependsOn"]!.Select(t => (string)t!).ToList()));
        }

        return errors.Count > 0
            ? new ReleaseSetResult(null, errors)
            : await ReleaseBuilder.ComposeAsync(path, (string)body["label"]!, createdAt, sources, cancellationToken);
    }
}
