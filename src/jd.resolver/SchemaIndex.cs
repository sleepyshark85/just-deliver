using Newtonsoft.Json.Linq;
using NJsonSchema;
using NJsonSchema.References;
using NJsonSchema.Validation;

namespace jd.resolver;

/// <summary>
/// The JSON Schemas embedded in this assembly (see jd.resolver.csproj), built once (docs/architecture/documents.md section 3): each is checked
/// against the meta-schema, a reference to another resolves by that schema's <c>$id</c>, and each document kind maps to the schema that declares
/// it in its <c>kind</c> property's <c>enum</c>. A schema that declares no kind is a shared part the others refer to; no kind or schema name is
/// spelled in code. The schemas ship with jd, so a schema without an <c>$id</c> or one the meta-schema refuses, an <c>$id</c> or a kind declared
/// twice, or a reference to no schema is a mistake in jd, not in a user's file: building throws <see cref="InvalidOperationException"/>.
/// </summary>
internal sealed class SchemaIndex
{
    // The annotation keyword and the one annotation the validator reads (documents.md section 3); the meta-schema is named after the keyword.
    private const string Annotation = "x-jd";
    private const string MessageKeyword = "message";
    private const string MetaSchemaName = $"schemas/{Annotation}.schema.json";

    private readonly Dictionary<string, JsonSchema> _byKind;

    private SchemaIndex(Dictionary<string, JsonSchema> byKind) => _byKind = byKind;

    /// <summary>The schemas jd ships, built once for the process; no caller's cancellation applies to a build every caller shares.</summary>
    public static Task<SchemaIndex> Shipped { get; } = BuildAsync(ReadAll().ToList());

    /// <summary>Validates <paramref name="document"/> against the schema of its kind, which must be one the schemas declare.</summary>
    public IReadOnlyList<LoadError> Validate(Document document) =>
        _byKind[document.Kind].Validate(document.Body).SelectMany(error => Errors(document.Source, error)).ToList();

    /// <summary>Builds the index from <paramref name="schemas"/>, each a name (for errors) and its text; the meta-schema is among them.</summary>
    internal static async Task<SchemaIndex> BuildAsync(IReadOnlyList<(string Name, string Text)> schemas)
    {
        var sources = schemas.Select(schema => Source.Parse(schema.Name, schema.Text)).ToList();
        var texts = ById(sources);
        var meta = await CompileAsync(texts, sources.Single(source => source.Name == MetaSchemaName).Id, CancellationToken.None);
        var refused = sources.SelectMany(source => meta.Validate(source.Tree).SelectMany(error => Errors(source.Name, error))).ToList();
        if (refused.Count > 0)
        {
            throw new InvalidOperationException($"The meta-schema refuses: {string.Join("; ", refused)}");
        }

        var byKind = new Dictionary<string, (string Name, JsonSchema Schema)>();
        foreach (var source in sources)
        {
            var kinds = source.Tree["properties"]?[DocumentLoader.KindField]?["enum"]?.Values<string>().OfType<string>().ToList() ?? [];
            if (kinds.Count == 0)
            {
                continue;
            }

            var schema = await CompileAsync(texts, source.Id, CancellationToken.None);
            foreach (var kind in kinds)
            {
                if (!byKind.TryAdd(kind, (source.Name, schema)))
                {
                    throw new InvalidOperationException($"Schemas '{byKind[kind].Name}' and '{source.Name}' both declare kind '{kind}'.");
                }
            }
        }

        return new SchemaIndex(byKind.ToDictionary(entry => entry.Key, entry => entry.Value.Schema));
    }

    // Each schema's text by its $id, the key a reference resolves by.
    private static Dictionary<string, string> ById(IReadOnlyList<Source> sources)
    {
        var names = new Dictionary<string, string>();
        foreach (var source in sources)
        {
            if (!names.TryAdd(source.Id, source.Name))
            {
                throw new InvalidOperationException($"Schemas '{names[source.Id]}' and '{source.Name}' both have the $id '{source.Id}'.");
            }
        }

        return sources.ToDictionary(source => source.Id, source => source.Text);
    }

    private static Task<JsonSchema> CompileAsync(Dictionary<string, string> texts, string id, CancellationToken cancellationToken) =>
        JsonSchema.FromJsonAsync(texts[id], id, schema => new ShippedReferences(schema, texts), cancellationToken);

    // Every problem is reported (documents.md section 6). A failing subschema with a message reports that text at its location. An error with
    // no message among its causes reports the validator's own text, as one error. One with a message among its causes reports each cause,
    // except the branches of a failed anyOf or oneOf that carry no message: the branch with the message is the one the value was meant for.
    private static IEnumerable<LoadError> Errors(string file, ValidationError error) => error switch
    {
        _ when Message(error.Schema) is { } message => [new LoadError(file, Location(error), message)],
        ChildSchemaValidationError child when CarriesMessage(child) => Causes(child).SelectMany(cause => Errors(file, cause)),
        _ => [new LoadError(file, Location(error), $"schema violation: {Detail(error)}")],
    };

    private static bool CarriesMessage(ValidationError error) =>
        Message(error.Schema) is not null || error is ChildSchemaValidationError child && child.Errors.Values.Any(causes => causes.Any(CarriesMessage));

    private static IEnumerable<ValidationError> Causes(ChildSchemaValidationError error) =>
        error.Kind is ValidationErrorKind.NotAnyOf or ValidationErrorKind.NotOneOf
            ? error.Errors.Values.Where(branch => branch.Any(CarriesMessage)).SelectMany(branch => branch)
            : error.Errors.Values.SelectMany(causes => causes);

    // The validator's text with its nested causes, on one line.
    private static string Detail(ValidationError error) =>
        string.Join(' ', error.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    // The validator's paths read "#/<dotted.path>"; the location is the dotted path.
    private static string Location(ValidationError error) => error.Path?.TrimStart('#').Trim('/') ?? string.Empty;

    private static string? Message(JsonSchema? schema) =>
        schema?.ExtensionData is { } data && data.TryGetValue(Annotation, out var value) && value is IDictionary<string, object?> annotations
            && annotations.TryGetValue(MessageKeyword, out var message)
            ? message as string
            : null;

    // Every resource of the assembly is a schema: jd.resolver.csproj embeds nothing else.
    private static IEnumerable<(string Name, string Text)> ReadAll()
    {
        var assembly = typeof(SchemaIndex).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // The assembly listed the name itself, so the resource exists.
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            yield return (name, reader.ReadToEnd());
        }
    }

    // A shipped schema: its name (for errors), its $id, and its text, parsed and as written.
    private sealed record Source(string Name, string Id, JObject Tree, string Text)
    {
        public static Source Parse(string name, string text)
        {
            var tree = JObject.Parse(text);
            return new Source(name, (string?)tree["$id"] ?? throw new InvalidOperationException($"Schema '{name}' has no $id."), tree, text);
        }
    }

    // Resolves a reference to another shipped schema by its $id, never from disk or the network.
    private sealed class ShippedReferences(JsonSchema root, Dictionary<string, string> texts)
        : JsonReferenceResolver(new JsonSchemaAppender(root, new DefaultTypeNameGenerator()))
    {
        public override async Task<IJsonReference> ResolveUrlReferenceAsync(string url, CancellationToken cancellationToken) =>
            texts.ContainsKey(url)
                ? await CompileAsync(texts, url, cancellationToken)
                : throw new InvalidOperationException($"No shipped schema has the $id '{url}'.");
    }
}
