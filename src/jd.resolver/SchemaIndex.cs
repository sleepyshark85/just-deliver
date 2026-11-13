using Newtonsoft.Json.Linq;
using NJsonSchema;
using NJsonSchema.References;
using NJsonSchema.Validation;

namespace jd.resolver;

/// <summary>
/// The JSON Schemas embedded in this assembly (see jd.resolver.csproj), built once (docs/architecture/documents.md section 3): each is checked
/// against the meta-schema, a reference to another resolves by that schema's <c>$id</c>, and each document kind maps to the schema that declares
/// it in its <c>kind</c> property's <c>enum</c>. A schema that declares no kind is a shared part the others refer to; no kind or schema name is
/// spelled in code. The schemas ship with jd, so a schema the meta-schema refuses, a kind declared twice or a reference to no schema is a mistake
/// in jd, not in a user's file: building throws <see cref="InvalidOperationException"/>.
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
    public static Task<SchemaIndex> Shipped { get; } = BuildAsync(ReadAll().ToList(), CancellationToken.None);

    /// <summary>Validates <paramref name="document"/> against the schema of its kind, which must be one the schemas declare.</summary>
    public IReadOnlyList<LoadError> Validate(Document document) =>
        _byKind[document.Kind].Validate(document.Body).SelectMany(error => Errors(document.Source, error)).ToList();

    /// <summary>Builds the index from <paramref name="schemas"/>, each a name (for errors) and its text; the meta-schema is among them.</summary>
    internal static async Task<SchemaIndex> BuildAsync(IReadOnlyList<(string Name, string Text)> schemas, CancellationToken cancellationToken)
    {
        var trees = schemas.Select(schema => (schema.Name, Tree: JObject.Parse(schema.Text))).ToList();
        var texts = trees.Zip(schemas).ToDictionary(pair => Id(pair.First.Tree), pair => pair.Second.Text);
        var meta = await CompileAsync(texts, Id(trees.Single(tree => tree.Name == MetaSchemaName).Tree), cancellationToken);
        var refused = trees.SelectMany(tree => meta.Validate(tree.Tree).SelectMany(error => Errors(tree.Name, error))).ToList();
        if (refused.Count > 0)
        {
            throw new InvalidOperationException($"The meta-schema refuses: {string.Join("; ", refused)}");
        }

        var byKind = new Dictionary<string, (string Name, JsonSchema Schema)>();
        foreach (var (name, tree) in trees)
        {
            var kinds = tree["properties"]?[DocumentLoader.KindField]?["enum"]?.Values<string>().OfType<string>().ToList() ?? [];
            if (kinds.Count == 0)
            {
                continue;
            }

            var schema = await CompileAsync(texts, Id(tree), cancellationToken);
            foreach (var kind in kinds)
            {
                if (!byKind.TryAdd(kind, (name, schema)))
                {
                    throw new InvalidOperationException($"Schemas '{byKind[kind].Name}' and '{name}' both declare kind '{kind}'.");
                }
            }
        }

        return new SchemaIndex(byKind.ToDictionary(entry => entry.Key, entry => entry.Value.Schema));
    }

    private static string Id(JObject tree) => (string?)tree["$id"] ?? throw new InvalidOperationException($"A schema has no $id: {tree["title"]}.");

    private static async Task<JsonSchema> CompileAsync(Dictionary<string, string> texts, string id, CancellationToken cancellationToken) =>
        await JsonSchema.FromJsonAsync(texts[id], id, schema => new ShippedReferences(schema, texts), cancellationToken);

    // An error whose failing subschema carries a message reports that text at its location (documents.md section 6), its causes unreported;
    // any other error reports the messages its causes carry or, when none does, the validator's own text.
    private static IEnumerable<LoadError> Errors(string file, ValidationError error)
    {
        var annotated = Annotated(file, error).ToList();
        if (annotated.Count > 0)
        {
            return annotated;
        }

        var detail = string.Join(' ', error.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return [new LoadError(file, Location(error), $"schema violation: {detail}")];
    }

    private static IEnumerable<LoadError> Annotated(string file, ValidationError error) =>
        Message(error.Schema) is { } message ? [new LoadError(file, Location(error), message)]
        : error is ChildSchemaValidationError child ? child.Errors.Values.SelectMany(causes => causes).SelectMany(cause => Annotated(file, cause))
        : [];

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
