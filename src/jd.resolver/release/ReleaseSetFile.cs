using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace jd.resolver.release;

/// <summary>Serializes a <see cref="ReleaseSet"/> and reads it back, re-verifying every embedded definition against its recorded hash.</summary>
public static class ReleaseSetFile
{
    // Bridge: the kind written and read here until the release documents are bound from their schemas (S24f).
    private const string Kind = "ReleaseSet";

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

    /// <summary>The set's document, as UTF-8 without a byte-order mark: what is written once and never changed.</summary>
    public static byte[] Serialize(ReleaseSet set)
    {
        var document = new Dictionary<string, object>
        {
            [DocumentLoader.KindField] = Kind,
            ["label"] = set.Label,
            ["createdAt"] = set.CreatedAt.ToString(ReleaseRecordFile.DocumentTimeFormat, CultureInfo.InvariantCulture),
            ["workloads"] = set.Workloads.Select(w => new Dictionary<string, object>
            {
                ["definitionSha256"] = w.DefinitionSha256,
                ["dependsOn"] = w.DependsOn,
                ["definition"] = w.Definition,
            }).ToList(),
        };

        // GetBytes writes no byte-order mark.
        return Encoding.UTF8.GetBytes(Serializer.Serialize(document));
    }

    /// <summary>Reads <paramref name="set"/>, its byte-order mark detected, and verifies it; errors name it. The result hands back its exact bytes.</summary>
    public static async Task<ReleaseSetResult> ParseAsync(SourceDocument set, CancellationToken cancellationToken = default)
    {
        var loaded = await DocumentLoader.LoadAsync(set.Name, set.Text(), [Kind], cancellationToken);
        if (loaded.Value is not { Body: var body })
        {
            return new ReleaseSetResult(null, loaded.Errors);
        }

        // The schema guarantees the shapes read here.
        if (!DateTimeOffset.TryParseExact((string)body["createdAt"]!, ReleaseRecordFile.DocumentTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAt))
        {
            return new ReleaseSetResult(null, [new LoadError(set.Name, "createdAt", "not a valid UTC time.")]);
        }

        // Only the definitions and what pins them are stored. Everything else (name, team, image, order) is derived from
        // the definitions by the same code that created the set, after each definition is checked against its hash.
        var errors = new List<LoadError>();
        var sources = new List<WorkloadSource>();
        foreach (var (item, i) in body["workloads"]!.Select((item, i) => (item, i)))
        {
            var definition = (string)item["definition"]!;
            var recorded = (string)item["definitionSha256"]!;
            var actual = ReleaseHash.Of(Encoding.UTF8.GetBytes(definition));
            if (actual != recorded)
            {
                errors.Add(new LoadError(set.Name, $"workloads[{i}].definition", $"does not match definitionSha256 (recorded {recorded}, content hashes to {actual}); the release set was modified."));
            }

            sources.Add(new WorkloadSource(set.Name, $"workloads[{i}].definition", recorded, definition, item["dependsOn"]!.Select(t => (string)t!).ToList()));
        }

        return errors.Count > 0
            ? new ReleaseSetResult(null, errors)
            : await ReleaseBuilder.ComposeAsync(set.Name, (string)body["label"]!, createdAt, sources, cancellationToken) with { Bytes = set.Content };
    }
}
