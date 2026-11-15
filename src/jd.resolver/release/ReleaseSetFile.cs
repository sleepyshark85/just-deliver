using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace jd.resolver.release;

/// <summary>
/// Writes a <see cref="ReleaseSet"/> and reads it back through the binder, by the <c>release.*</c> roles of the release set's schema, re-verifying
/// every embedded definition against its recorded hash.
/// </summary>
public static class ReleaseSetFile
{
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

    /// <summary>
    /// The set's document, as UTF-8 without a byte-order mark: what is written once and never changed. A set <see cref="ReleaseBuilder"/> made fits
    /// the schema, so an error writing it is a mistake in jd: throws <see cref="InvalidOperationException"/> naming each. Nothing it awaits can be
    /// cancelled: the schemas are built once for the process.
    /// </summary>
    public static async Task<byte[]> SerializeAsync(ReleaseSet set)
    {
        var index = await SchemaIndex.Shipped;
        var written = new Binder(index).WriteReleaseSet(nameof(ReleaseSet), Kind(index), set);
        if (written.Value is not { } document)
        {
            throw new InvalidOperationException($"The release set '{set.Label}' does not fit its schema: {string.Join("; ", written.Errors)}");
        }

        // GetBytes writes no byte-order mark.
        return Encoding.UTF8.GetBytes(DocumentYaml.Serialize(Serializer, document));
    }

    /// <summary>Reads <paramref name="set"/>, its byte-order mark detected, and verifies it; errors name it. The result hands back its exact bytes.</summary>
    public static async Task<ReleaseSetResult> ParseAsync(SourceDocument set, CancellationToken cancellationToken = default)
    {
        var index = await SchemaIndex.Shipped;
        var loaded = await DocumentLoader.LoadAsync(set.Name, set.Text(), [Kind(index)], cancellationToken);
        if (loaded.Value is not { } document)
        {
            return new ReleaseSetResult(null, loaded.Errors);
        }

        var binder = new Binder(index);
        var release = binder.ReadRelease(document);
        if (!DateTimeOffset.TryParseExact(release.CreatedAt, ReleaseRecordFile.DocumentTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAt))
        {
            return new ReleaseSetResult(null, [new LoadError(set.Name, release.CreatedAtLocation, "not a valid UTC time.")]);
        }

        // Only the definitions and what pins them are stored. Everything else (name, team, image, order) is derived from
        // the definitions by the same code that created the set, after each definition is checked against its hash.
        var errors = new List<LoadError>();
        var sources = new List<WorkloadSource>();
        foreach (var entry in release.Workloads)
        {
            var actual = ReleaseHash.Of(Encoding.UTF8.GetBytes(entry.Definition));
            if (actual != entry.DefinitionSha256)
            {
                errors.Add(new LoadError(set.Name, entry.DefinitionLocation, $"does not match {binder.FieldName(document.Kind, Roles.ReleaseDefinitionSha256)} (recorded {entry.DefinitionSha256}, content hashes to {actual}); the release set was modified."));
            }

            sources.Add(new WorkloadSource(set.Name, entry.DefinitionLocation, entry.DefinitionSha256, entry.Definition, entry));
        }

        return errors.Count > 0
            ? new ReleaseSetResult(null, errors)
            : await ReleaseBuilder.ComposeAsync(set.Name, release, createdAt, sources, cancellationToken) with { Bytes = set.Content };
    }

    // The release set's kind: the one kind that embeds the definitions.
    private static string Kind(SchemaIndex index) => index.KindsBinding(Roles.ReleaseDefinition).Single();
}
