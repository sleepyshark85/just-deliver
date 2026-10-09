using System.Text.RegularExpressions;
using jd.definitionvalidator;
using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>
/// Builds a <see cref="Catalog"/> from catalog files held in memory. Pure: reading the directory is
/// <see cref="CatalogDirectory"/>'s job. Every problem in every file is collected, so a catalog can be
/// fixed in one pass.
/// </summary>
public static partial class CatalogParser
{
    private const string CatalogKind = "Catalog";
    private const string ResourceTypeKind = "ResourceType";
    private const string MappingKind = "Mapping";
    private const string PolicyKind = "Policy";
    private const string NamingKind = "Naming";
    private const string RolesKind = "Roles";

    private static readonly string[] Kinds = [CatalogKind, ResourceTypeKind, MappingKind, PolicyKind, NamingKind, RolesKind];

    private static readonly YamlSchemaValidator Validator = new();

    // Schema errors read "<Kind>: #/<dotted.path>" (nested ones follow in braces); the first path is the location.
    [GeneratedRegex(@"^\w+: #(?<path>\S*)")]
    private static partial Regex SchemaErrorRegex();

    private sealed record Document(string Source, string Kind, JObject Body);

    public static async Task<CatalogLoadResult> ParseAsync(IEnumerable<CatalogSource> sources, CancellationToken cancellationToken = default)
    {
        var errors = new List<CatalogError>();
        var documents = new List<Document>();
        foreach (var source in sources.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            var document = await ReadDocumentAsync(source, errors, cancellationToken);
            if (document is not null)
            {
                documents.Add(document);
            }
        }

        var catalogs = documents.Where(d => d.Kind == CatalogKind).ToList();
        var types = documents.Where(d => d.Kind == ResourceTypeKind).Select(ToResourceType).ToList();
        var mappings = documents.Where(d => d.Kind == MappingKind).Select(ToMapping).ToList();
        var policies = documents.Where(d => d.Kind == PolicyKind).Select(ToPolicy).ToList();
        var naming = Merge(documents.Where(d => d.Kind == NamingKind), "rules", ToNamingRule, errors);
        var roles = Merge(documents.Where(d => d.Kind == RolesKind), "roles", role => (string?)role ?? string.Empty, errors);

        CheckSingleCatalog(catalogs, errors);
        CheckUnique(types, t => t.Source, t => t.Name, "ResourceType", errors);
        CheckUnique(policies, p => p.Source, p => p.Name, "Policy", errors);
        CheckMappingTypes(mappings, types, errors);

        if (errors.Count > 0)
        {
            return new CatalogLoadResult(null, errors);
        }

        var version = (string?)catalogs[0].Body["version"] ?? string.Empty;
        var catalog = new Catalog(
            version,
            types,
            mappings,
            policies,
            naming,
            roles);
        return new CatalogLoadResult(catalog, errors);
    }

    private static async Task<Document?> ReadDocumentAsync(CatalogSource source, List<CatalogError> errors, CancellationToken cancellationToken)
    {
        JToken parsed;
        try
        {
            parsed = YamlSchemaValidator.ParseYaml(source.Content);
        }
        catch (Exception ex)
        {
            errors.Add(new CatalogError(source.Path, string.Empty, $"not valid YAML: {ex.Message}"));
            return null;
        }

        if (parsed is not JObject body)
        {
            errors.Add(new CatalogError(source.Path, string.Empty, "must be one YAML mapping with a 'kind' key."));
            return null;
        }

        if (body["kind"] is not { Type: JTokenType.String } kindToken)
        {
            errors.Add(new CatalogError(source.Path, "kind", $"required; one of {string.Join(", ", Kinds)}."));
            return null;
        }

        var kind = (string?)kindToken ?? string.Empty;
        if (!Kinds.Contains(kind))
        {
            errors.Add(new CatalogError(source.Path, "kind", $"unknown kind '{kind}'; expected one of {string.Join(", ", Kinds)}."));
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await Validator.ValidateContentAsync(await ReadSchemaAsync(kind, cancellationToken), source.Content);
        if (!result.IsValid)
        {
            errors.AddRange(result.Errors.Select(e => ToSchemaError(source.Path, e)));
            return null;
        }

        return new Document(source.Path, kind, body);
    }

    private static CatalogError ToSchemaError(string file, string schemaError)
    {
        var path = SchemaErrorRegex().Match(schemaError).Groups["path"].Value;
        var location = path.Trim('/');
        var detail = string.Join(' ', schemaError.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return new CatalogError(file, location, $"schema violation: {detail}");
    }

    private static async Task<string> ReadSchemaAsync(string kind, CancellationToken cancellationToken)
    {
        var name = $"schemas/catalog/{kind.ToLowerInvariant()}.schema.json";
        await using var stream = typeof(CatalogParser).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Schema '{name}' is not embedded in jd.resolver.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    // The checks below run on documents that passed their schema, so the shapes they read are guaranteed.

    private static void CheckSingleCatalog(List<Document> catalogs, List<CatalogError> errors)
    {
        if (catalogs.Count == 0)
        {
            errors.Add(new CatalogError("(catalog)", string.Empty, $"no file of kind {CatalogKind}; exactly one is required."));
        }

        foreach (var extra in catalogs.Skip(1))
        {
            errors.Add(new CatalogError(extra.Source, "kind", $"a second {CatalogKind} file; exactly one is allowed (first: {catalogs[0].Source})."));
        }
    }

    private static void CheckUnique<T>(List<T> items, Func<T, string> source, Func<T, string> key, string kind, List<CatalogError> errors)
    {
        foreach (var group in items.GroupBy(key))
        {
            foreach (var duplicate in group.Skip(1))
            {
                errors.Add(new CatalogError(source(duplicate), "name", $"{kind} '{group.Key}' is already declared in {source(group.First())}."));
            }
        }
    }

    private static void CheckMappingTypes(List<Mapping> mappings, List<ResourceType> types, List<CatalogError> errors)
    {
        var declared = types.Select(t => t.Name).Distinct().Order().ToList();
        foreach (var mapping in mappings)
        {
            if (mapping.Match.Criteria.TryGetValue("type", out var type) && !declared.Contains(type))
            {
                var known = declared.Count == 0 ? "none" : string.Join(", ", declared);
                errors.Add(new CatalogError(mapping.Source, "match.type", $"'{type}' is not a declared ResourceType (declared: {known})."));
            }
        }
    }

    // Naming and Roles files are merged; the same key in two files is ambiguous, so it is an error.
    private static Dictionary<string, T> Merge<T>(IEnumerable<Document> documents, string property, Func<JToken, T> convert, List<CatalogError> errors)
    {
        var merged = new Dictionary<string, T>();
        var origin = new Dictionary<string, string>();
        foreach (var document in documents)
        {
            foreach (var entry in Map(document.Body[property]))
            {
                if (origin.TryGetValue(entry.Key, out var first))
                {
                    errors.Add(new CatalogError(document.Source, $"{property}.{entry.Key}", $"already defined in {first}."));
                    continue;
                }

                origin[entry.Key] = document.Source;
                merged[entry.Key] = convert(entry.Value);
            }
        }

        return merged;
    }

    private static ResourceType ToResourceType(Document d) => new(
        d.Source,
        Text(d.Body["name"]),
        Text(d.Body["description"]),
        Strings(d.Body["classes"]),
        Strings(d.Body["exports"]));

    private static Mapping ToMapping(Document d) => new(
        d.Source,
        ToMatch(d.Body["match"]),
        Map(d.Body["nodes"]).ToDictionary(e => e.Key, e => ToNode(e.Value)),
        Map(d.Body["exports"]).ToDictionary(e => e.Key, e => Text(e.Value)));

    private static Policy ToPolicy(Document d) => new(
        d.Source,
        Text(d.Body["name"]),
        Text(d.Body["reason"]),
        ToMatch(d.Body["match"]),
        Map(d.Body["set"]),
        Map(d.Body["default"]),
        Map(d.Body["add"]).ToDictionary(e => e.Key, e => ToNode(e.Value)));

    private static NamingRule ToNamingRule(JToken rule) => new(Text(rule["pattern"]), (int?)rule["maxLength"] ?? 0, Text(rule["allowed"]));

    private static Match ToMatch(JToken? match) => new(Map(match).ToDictionary(e => e.Key, e => Text(e.Value)));

    private static Node ToNode(JToken node) => new(
        Text(node["template"]),
        Enum.Parse<NodeKind>((string?)node["kind"] ?? nameof(NodeKind.Create), ignoreCase: true),
        Map(node["config"]));

    private static string Text(JToken? token) => (string?)token ?? string.Empty;

    private static List<string> Strings(JToken? token) => token is JArray array ? array.Select(Text).ToList() : [];

    private static Dictionary<string, JToken> Map(JToken? token) =>
        token is JObject obj ? obj.Properties().ToDictionary(p => p.Name, p => p.Value) : [];
}
