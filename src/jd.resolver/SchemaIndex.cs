using System.Collections.Immutable;
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
/// twice, or a reference to no schema is a mistake in jd, not in a user's file: building throws <see cref="InvalidOperationException"/>. Per kind,
/// the index also keeps the fields its <c>x-jd</c> annotations name: those that define names, those that refer to them, and whether the kind has
/// exactly one document (<see cref="NamesOf"/>); the fields that hold a role, with the <c>select</c> maps enclosing each (<see cref="RolesOf"/>);
/// and the <see cref="Layers"/>.
/// </summary>
internal sealed class SchemaIndex
{
    // The annotation keyword and the one annotation the validator reads (documents.md section 3); the meta-schema is named after the keyword.
    private const string Annotation = "x-jd";
    private const string MessageKeyword = "message";
    private const string DefineKeyword = "define";
    private const string RefKeyword = "ref";
    private const string FallbackKeyword = "fallback";
    private const string OneKeyword = "one";
    private const string IsKeyword = "is";
    private const string SelectKeyword = "select";
    private const string ScopeKeyword = "scope";
    private const string MetaSchemaName = $"schemas/{Annotation}.schema.json";

    private readonly Dictionary<string, JsonSchema> _byKind;
    private readonly Dictionary<string, KindNames> _names;
    private readonly Dictionary<string, List<RoleField>> _roles;

    private SchemaIndex(Dictionary<string, JsonSchema> byKind)
    {
        _byKind = byKind;
        var fields = byKind.ToDictionary(entry => entry.Key, entry => Fields(entry.Value));
        var selects = fields.ToDictionary(entry => entry.Key, entry => Selects(entry.Value));
        _names = byKind.ToDictionary(entry => entry.Key, entry => Annotated(entry.Value, fields[entry.Key], selects[entry.Key]));
        _roles = fields.ToDictionary(entry => entry.Key, entry => RoleFields(entry.Value, selects[entry.Key]));
        Layers = fields.Values.SelectMany(kind => kind).Where(field => Keyword(field.Schema, IsKeyword) is Roles.Layer)
            .Select(field => field.Schema.Enumeration.OfType<string>().ToList()).FirstOrDefault() ?? [];
    }

    /// <summary>The schemas jd ships, built once for the process; no caller's cancellation applies to a build every caller shares.</summary>
    public static Task<SchemaIndex> Shipped { get; } = BuildAsync(ReadAll().ToList());

    /// <summary>Validates <paramref name="document"/> against the schema of its kind, which must be one the schemas declare.</summary>
    public IReadOnlyList<LoadError> Validate(Document document) =>
        _byKind[document.Kind].Validate(document.Body).SelectMany(error => Errors(document.Source, error)).ToList();

    /// <summary>The name annotations of <paramref name="kind"/>, which must be one the schemas declare.</summary>
    public KindNames NamesOf(string kind) => _names[kind];

    /// <summary>The fields of <paramref name="kind"/>, which must be one the schemas declare, that hold a role (<c>is</c>).</summary>
    public IReadOnlyList<RoleField> RolesOf(string kind) => _roles[kind];

    /// <summary>The layers, in deploy order: the <c>enum</c> of the subschema that holds the <see cref="Roles.Layer"/> role.</summary>
    public IReadOnlyList<string> Layers { get; }

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

    private static string? Message(JsonSchema? schema) => Keyword(schema, MessageKeyword) as string;

    private static object? Keyword(JsonSchema? schema, string keyword) =>
        Annotations(schema) is { } annotations && annotations.TryGetValue(keyword, out var found) ? found : null;

    private static IDictionary<string, object?>? Annotations(JsonSchema? schema) =>
        schema?.ExtensionData is { } data && data.TryGetValue(Annotation, out var value) ? value as IDictionary<string, object?> : null;

    // Whether a subschema carries an annotation the engine reads from documents: every keyword but message, which the validator reads.
    private static bool Binds(JsonSchema schema) => Annotations(schema)?.Keys.Any(keyword => keyword != MessageKeyword) == true;

    // A reference's fallback is its map's select fallback: one annotation, read once, for both rules.
    private static KindNames Annotated(JsonSchema root, List<Field> fields, List<SelectField> selects)
    {
        // define takes one namespace or a list of them (the meta-schema's two forms).
        var defines = fields.SelectMany(field => (Keyword(field.Schema, DefineKeyword) switch
        {
            string single => [single],
            IEnumerable<object> list => list.OfType<string>(),
            _ => [],
        }).Select(space => new NameField(space, field.Path, Fallback: null)));
        var refs = fields.SelectMany(field => Keyword(field.Schema, RefKeyword) is string space
            ? [new NameField(space, field.Path, selects.FirstOrDefault(select => select.Path == field.Path)?.Fallback)]
            : Array.Empty<NameField>());
        return new KindNames(Keyword(root, OneKeyword) is true, [.. defines], [.. refs]);
    }

    // A part's keys are read by depth (PartOwner.Select), so two select maps at one depth are refused.
    private static List<SelectField> Selects(List<Field> fields)
    {
        var selects = fields.SelectMany(field => Keyword(field.Schema, SelectKeyword) is string role
            ? [new SelectField(role, field.Path, Keyword(field.Schema, FallbackKeyword) as string)]
            : Array.Empty<SelectField>()).Distinct().ToList();
        var level = selects.GroupBy(select => selects.Count(outer => select.Path.StartsWith($"{outer.Path}.*", StringComparison.Ordinal)));
        if (level.FirstOrDefault(group => group.Count() > 1) is { } same)
        {
            throw new InvalidOperationException($"The select maps at {string.Join(" and ", same.Select(select => select.Path))} are at the same depth; a part's keys are read by depth.");
        }

        return selects;
    }

    // Each role once per path, with what every subschema at that path says: the scope, the default, and the select maps whose entries the
    // path is in, outermost first.
    private static List<RoleField> RoleFields(List<Field> fields, List<SelectField> selects) =>
        fields.GroupBy(field => field.Path).SelectMany(at => at.Select(field => Keyword(field.Schema, IsKeyword)).OfType<string>().Distinct().Select(role => new RoleField(
            role,
            at.Key,
            at.Select(field => Keyword(field.Schema, ScopeKeyword)).OfType<string>().FirstOrDefault(),
            at.Select(field => field.Schema.Default).FirstOrDefault(value => value is not null) is { } value ? JToken.FromObject(value) : null,
            [.. selects.Where(select => at.Key.StartsWith($"{select.Path}.*", StringComparison.Ordinal))]))).ToList();

    // Every subschema of a kind with the path (JSONPath) of the values it describes: a property adds its name, a map's subschema stands for
    // each of its values and a list's for each item, and a reference or a branch describes the same values as its parent. A recursive schema
    // (a value holding values of its own shape, such as a descriptor's values) is walked down to where it repeats. An annotation in it would
    // be read at its first level only, and of two annotated value schemas of one map each would be read for every key: both are refused.
    private static List<Field> Fields(JsonSchema root)
    {
        var fields = new List<Field>();
        var repeated = new HashSet<JsonSchema>();
        Walk(root, "$", [], fields, repeated);
        if (fields.FirstOrDefault(field => Binds(field.Schema) && field.Within.Overlaps(repeated)) is { } recursive)
        {
            throw new InvalidOperationException($"The annotation at {recursive.Path} is in a recursive schema, so it would be read at its first level only.");
        }

        return fields;
    }

    private static void Walk(JsonSchema schema, string path, ImmutableHashSet<JsonSchema> outer, List<Field> fields, HashSet<JsonSchema> repeated)
    {
        if (outer.Contains(schema))
        {
            repeated.Add(schema);
            return;
        }

        var within = outer.Add(schema);
        fields.Add(new Field(schema, path, within));
        foreach (var (name, property) in schema.Properties)
        {
            Walk(property, $"{path}['{name}']", within, fields, repeated);
        }

        var annotatedValues = 0;
        foreach (var value in schema.PatternProperties.Values.Append(schema.AdditionalPropertiesSchema).OfType<JsonSchema>())
        {
            var first = fields.Count;
            Walk(value, $"{path}.*", within, fields, repeated);
            annotatedValues += fields.Skip(first).Any(field => Binds(field.Schema)) ? 1 : 0;
        }

        if (annotatedValues > 1)
        {
            throw new InvalidOperationException($"The map at {path} has {annotatedValues} annotated value schemas; each would be read for every key at {path}.*.");
        }

        foreach (var item in new[] { schema.Item }.OfType<JsonSchema>())
        {
            Walk(item, $"{path}[*]", within, fields, repeated);
        }

        foreach (var same in schema.AllOf.Concat(schema.AnyOf).Concat(schema.OneOf).Append(schema.Reference).OfType<JsonSchema>())
        {
            Walk(same, path, within, fields, repeated);
        }
    }

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

    // A subschema of a kind, the path (JSONPath) of the values it describes, and the subschemas it is in, itself included.
    private sealed record Field(JsonSchema Schema, string Path, ImmutableHashSet<JsonSchema> Within);

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
