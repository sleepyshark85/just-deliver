using System.Text.RegularExpressions;
using jd.definitionvalidator;
using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>
/// Builds a <see cref="Catalog"/> from catalog files held in memory. Pure: reading the directory is
/// <see cref="CatalogDirectory"/>'s job. Every problem in every file is collected, so a catalog can be
/// fixed in one pass.
/// </summary>
public static class CatalogParser
{
    private const string CatalogKind = "Catalog";
    private const string ResourceTypeKind = "ResourceType";
    private const string MappingKind = "Mapping";
    private const string PolicyKind = "Policy";
    private const string NamingKind = "Naming";
    private const string RolesKind = "Roles";

    private static readonly string[] Kinds = [CatalogKind, ResourceTypeKind, MappingKind, PolicyKind, NamingKind, RolesKind];

    private static readonly YamlSchemaValidator Validator = new();

    private static readonly Regex NodeNamePattern = new("^[a-z]([a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant);

    // Valid is false when the file failed its schema. Such a file still counts toward the cross-file checks
    // that only need its kind or name, so one mistake is not reported a second time as a missing or undeclared item.
    private sealed record Document(string Source, string Kind, JObject Body, bool Valid);

    public static async Task<CatalogLoadResult> ParseAsync(IEnumerable<CatalogSource> sources, CancellationToken cancellationToken = default)
    {
        var errors = new List<LoadError>();
        var documents = new List<Document>();
        var unreadable = 0;
        foreach (var source in sources.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            var document = await ReadDocumentAsync(source, errors, cancellationToken);
            if (document is null)
            {
                unreadable++;
            }
            else
            {
                documents.Add(document);
            }
        }

        var catalogs = OfKind(documents, CatalogKind);
        var typeDocuments = OfKind(documents, ResourceTypeKind);
        var policyDocuments = OfKind(documents, PolicyKind);
        var types = typeDocuments.Where(d => d.Valid).Select(ToResourceType).ToList();
        var mappings = OfKind(documents, MappingKind).Where(d => d.Valid).Select(ToMapping).ToList();
        var policies = policyDocuments.Where(d => d.Valid).Select(ToPolicy).ToList();
        var namingDocuments = OfKind(documents, NamingKind).Where(d => d.Valid).ToList();
        var naming = Merge(namingDocuments, "rules", ToNamingRule, errors);
        CheckNamingAllowed(namingDocuments, errors);
        var roles = Merge(OfKind(documents, RolesKind).Where(d => d.Valid), "roles", role => (string?)role ?? string.Empty, errors);

        // A file whose kind could not be read might have been the missing Catalog, so do not claim it is missing.
        CheckSingleCatalog(catalogs, unreadable == 0, errors);
        CheckUnique(typeDocuments, "ResourceType", errors);
        CheckUnique(policyDocuments, "Policy", errors);
        CheckMappingTypes(mappings, typeDocuments, errors);
        CheckMappingMatches(mappings, errors);
        CheckPolicyScopes(policies, errors);
        CheckPolicyPaths(policies, errors);
        CheckNodeNames(mappings, policies, errors);
        CheckRuntimeNode(mappings, policies, errors);
        CheckExportsContract(mappings, types, errors);

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

    private static async Task<Document?> ReadDocumentAsync(CatalogSource source, List<LoadError> errors, CancellationToken cancellationToken)
    {
        JToken parsed;
        try
        {
            parsed = YamlSchemaValidator.ParseYaml(source.Content);
        }
        catch (Exception ex)
        {
            errors.Add(new LoadError(source.Path, string.Empty, $"not valid YAML: {YamlSchemaValidator.DescribeYamlError(ex, source.Content)}"));
            return null;
        }

        if (parsed is not JObject body)
        {
            errors.Add(new LoadError(source.Path, string.Empty, "must be one YAML mapping with a 'kind' key."));
            return null;
        }

        if (body["kind"] is not { Type: JTokenType.String } kindToken)
        {
            errors.Add(new LoadError(source.Path, "kind", $"required; one of {string.Join(", ", Kinds)}."));
            return null;
        }

        var kind = (string?)kindToken ?? string.Empty;
        if (!Kinds.Contains(kind))
        {
            // Readable, just not ours: it is not a missing Catalog, and no kind-specific check applies to it.
            errors.Add(new LoadError(source.Path, "kind", $"unknown kind '{kind}'; expected one of {string.Join(", ", Kinds)}."));
            return new Document(source.Path, kind, body, false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await Validator.ValidateTokenAsync(await EmbeddedSchema.ReadAsync($"schemas/catalog/{kind.ToLowerInvariant()}.schema.json", cancellationToken), body);
        errors.AddRange(result.Errors.Select(e => LoadError.FromSchema(source.Path, e)));
        return new Document(source.Path, kind, body, result.IsValid);
    }

    private static List<Document> OfKind(List<Document> documents, string kind) => documents.Where(d => d.Kind == kind).ToList();

    // Valid documents have the shapes the schemas guarantee; invalid ones are only asked for what they may lack.

    private static void CheckSingleCatalog(List<Document> catalogs, bool allFilesReadable, List<LoadError> errors)
    {
        if (catalogs.Count == 0 && allFilesReadable)
        {
            errors.Add(new LoadError("(catalog)", string.Empty, $"no file of kind {CatalogKind}; exactly one is required."));
        }

        foreach (var extra in catalogs.Skip(1))
        {
            errors.Add(new LoadError(extra.Source, "kind", $"a second {CatalogKind} file; exactly one is allowed (first: {catalogs[0].Source})."));
        }
    }

    private static string? NameOf(Document d) => d.Body["name"] is { Type: JTokenType.String } name ? (string?)name : null;

    private static void CheckUnique(List<Document> documents, string kind, List<LoadError> errors)
    {
        foreach (var group in documents.Where(d => NameOf(d) is not null).GroupBy(d => NameOf(d)))
        {
            foreach (var duplicate in group.Skip(1))
            {
                errors.Add(new LoadError(duplicate.Source, "name", $"{kind} '{group.Key}' is already declared in {group.First().Source}."));
            }
        }
    }

    // Inside one policy, a path and one of its sub-paths would make the result depend on application order.
    private static void CheckPolicyPaths(List<Policy> policies, List<LoadError> errors)
    {
        foreach (var policy in policies)
        {
            foreach (var (field, entries) in new[] { ("set", policy.Set), ("default", policy.Default) })
            {
                foreach (var outer in entries.Keys.Order(StringComparer.Ordinal))
                {
                    foreach (var inner in entries.Keys.Order(StringComparer.Ordinal).Where(k => k.StartsWith(outer + ".", StringComparison.Ordinal)))
                    {
                        errors.Add(new LoadError(policy.Source, $"{field}.{outer}", $"overlaps '{inner}' in the same policy; give one of them."));
                    }
                }
            }
        }
    }

    // Node names become parts of graph node ids and backend stack names, so they are lowercase kebab. Enforced here, not in the
    // schema: the schema validator ignores the propertyNames keyword.
    private static void CheckNodeNames(List<Mapping> mappings, List<Policy> policies, List<LoadError> errors)
    {
        var named = mappings.Select(m => (m.Source, Field: "nodes", Names: m.Nodes.Keys))
            .Concat(policies.Select(p => (p.Source, Field: "add", Names: p.Add.Keys)));
        foreach (var (source, field, names) in named)
        {
            foreach (var name in names.Where(n => !NodeNamePattern.IsMatch(n)))
            {
                errors.Add(new LoadError(source, $"{field}.{name}", $"'{name}' is not a valid node name; use lowercase letters, digits and hyphens, starting with a letter and not ending in a hyphen."));
            }
        }
    }

    // The node named 'runtime' is the workload's runtime: a runtime mapping must declare it, nothing else may (the mapping's
    // other nodes and the policy-added nodes share its scope, and every ${runtime.<output>} points to it).
    private static void CheckRuntimeNode(List<Mapping> mappings, List<Policy> policies, List<LoadError> errors)
    {
        const string reserved = ExpressionEvaluator.RuntimeNode;
        foreach (var mapping in mappings)
        {
            if (!mapping.IsRuntime)
            {
                if (mapping.Nodes.ContainsKey(reserved))
                {
                    errors.Add(new LoadError(mapping.Source, $"nodes.{reserved}", $"'{reserved}' is the runtime node, declared only by a runtime mapping ('{MatchCriteria.KindKey}: {MatchCriteria.RuntimeKind}')."));
                }

                if (mapping.Probe is not null)
                {
                    errors.Add(new LoadError(mapping.Source, "probe", "only a runtime mapping has a probe."));
                }

                continue;
            }

            if (!mapping.Nodes.ContainsKey(reserved))
            {
                errors.Add(new LoadError(mapping.Source, "nodes", $"a runtime mapping must declare a node named '{reserved}'."));
            }

            if (mapping.Exports.Count > 0)
            {
                errors.Add(new LoadError(mapping.Source, "exports", "a runtime mapping has no exports."));
            }
        }

        foreach (var policy in policies.Where(p => p.Add.ContainsKey(reserved)))
        {
            errors.Add(new LoadError(policy.Source, $"add.{reserved}", $"'{reserved}' is the runtime node, declared only by a runtime mapping."));
        }
    }

    // Every policy must apply to node scope or workload scope (resolver.md, Policy scope); one that fits neither would silently do nothing.
    private static void CheckPolicyScopes(List<Policy> policies, List<LoadError> errors)
    {
        foreach (var policy in policies)
        {
            var criteria = policy.Match.Criteria;
            var kind = criteria.GetValueOrDefault(MatchCriteria.KindKey);
            void Fail(string location, string message) => errors.Add(new LoadError(policy.Source, location, message));

            if (kind is not null && kind != MatchCriteria.RuntimeKind)
            {
                Fail("match.kind", $"'{kind}' is not a policy scope; the only 'kind' a policy can match is '{MatchCriteria.RuntimeKind}'.");
            }
            else if (kind is null)
            {
                if (criteria.ContainsKey(MatchCriteria.RuntimeKey))
                {
                    Fail("match.runtime", $"'runtime' needs 'kind: {MatchCriteria.RuntimeKind}'; node-scope policies match type, class, tier and template.");
                }

                if (policy.Add.Count > 0)
                {
                    Fail("add", $"a node-scope policy cannot add nodes; use 'kind: {MatchCriteria.RuntimeKind}' to add nodes once per workload.");
                }
            }
            else
            {
                var node = criteria.Keys.Where(k => k is MatchCriteria.TypeKey or MatchCriteria.ClassKey or MatchCriteria.TemplateKey).Order().ToList();
                if (node.Count > 0)
                {
                    Fail("match", $"a 'kind: {MatchCriteria.RuntimeKind}' policy cannot also match {string.Join(", ", node)}.");
                }

                foreach (var (field, entries) in new[] { ("set", policy.Set), ("default", policy.Default) })
                {
                    foreach (var path in entries.Keys.Where(k => !k.StartsWith(MatchCriteria.RuntimeKey + ".", StringComparison.Ordinal)))
                    {
                        Fail($"{field}.{path}", $"a 'kind: {MatchCriteria.RuntimeKind}' policy can only set or default fields under '{MatchCriteria.RuntimeKey}.', not '{path}'.");
                    }
                }
            }
        }
    }

    // A mapping with neither would match every type and skip the exports contract check.
    private static void CheckMappingMatches(List<Mapping> mappings, List<LoadError> errors)
    {
        foreach (var mapping in mappings.Where(m => !m.Match.Criteria.ContainsKey(MatchCriteria.TypeKey) && !m.Match.Criteria.ContainsKey(MatchCriteria.KindKey)))
        {
            errors.Add(new LoadError(mapping.Source, "match", $"must have '{MatchCriteria.TypeKey}' (a requirement mapping) or '{MatchCriteria.KindKey}' (a runtime mapping)."));
        }
    }

    private static void CheckMappingTypes(List<Mapping> mappings, List<Document> typeDocuments, List<LoadError> errors)
    {
        var declared = typeDocuments.Select(NameOf).OfType<string>().Distinct().Order().ToList();
        foreach (var mapping in mappings)
        {
            if (mapping.Match.Criteria.TryGetValue(MatchCriteria.TypeKey, out var type) && !declared.Contains(type))
            {
                var known = declared.Count == 0 ? "none" : string.Join(", ", declared);
                errors.Add(new LoadError(mapping.Source, "match.type", $"'{type}' is not a declared ResourceType (declared: {known})."));
            }
        }
    }

    // A mapping must export exactly what its type promises, so ${resource.<id>.<export>} always resolves.
    private static void CheckExportsContract(List<Mapping> mappings, List<ResourceType> types, List<LoadError> errors)
    {
        foreach (var mapping in mappings)
        {
            if (!mapping.Match.Criteria.TryGetValue(MatchCriteria.TypeKey, out var name) || types.FirstOrDefault(t => t.Name == name) is not { } type)
            {
                continue;
            }

            var missing = type.Exports.Where(e => !mapping.Exports.ContainsKey(e)).ToList();
            var extra = mapping.Exports.Keys.Where(e => !type.Exports.Contains(e)).ToList();
            if (missing.Count > 0 || extra.Count > 0)
            {
                var problems = new[] { missing.Count > 0 ? $"missing {string.Join(", ", missing)}" : null, extra.Count > 0 ? $"not in the type: {string.Join(", ", extra)}" : null };
                errors.Add(new LoadError(mapping.Source, "exports", $"must export exactly the exports of type '{name}' ({string.Join(", ", type.Exports)}); {string.Join("; ", problems.OfType<string>())}."));
            }
        }
    }

    // A truncated name ends in a lowercase hex hash that is appended after filtering, so every rule must allow those digits.
    private const string HashDigits = "0123456789abcdef";

    private static void CheckNamingAllowed(List<Document> documents, List<LoadError> errors)
    {
        foreach (var document in documents)
        {
            foreach (var (kind, rule) in Map(document.Body["rules"]))
            {
                var location = $"rules.{kind}.allowed";
                var allowed = Text(rule["allowed"]);
                try
                {
                    var regex = new Regex(allowed);
                    var rejected = HashDigits.Where(c => !regex.IsMatch(c.ToString())).ToList();
                    if (rejected.Count > 0)
                    {
                        errors.Add(new LoadError(document.Source, location, $"'{allowed}' must allow every hash digit 0-9a-f; it rejects {string.Concat(rejected)}."));
                    }
                }
                catch (ArgumentException e)
                {
                    errors.Add(new LoadError(document.Source, location, $"'{allowed}' is not a valid regex character class: {e.Message}"));
                }
            }
        }
    }

    // Naming and Roles files are merged; the same key in two files is ambiguous, so it is an error.
    private static Dictionary<string, T> Merge<T>(IEnumerable<Document> documents, string property, Func<JToken, T> convert, List<LoadError> errors)
    {
        var merged = new Dictionary<string, T>();
        var origin = new Dictionary<string, string>();
        foreach (var document in documents)
        {
            foreach (var entry in Map(document.Body[property]))
            {
                if (origin.TryGetValue(entry.Key, out var first))
                {
                    errors.Add(new LoadError(document.Source, $"{property}.{entry.Key}", $"already defined in {first}."));
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
        Map(d.Body["exports"]).ToDictionary(e => e.Key, e => Text(e.Value)),
        d.Body["probe"] is { } probe ? new Probe(Text(probe["path"]), (int?)probe["expectedStatus"] ?? 0) : null);

    private static Policy ToPolicy(Document d) => new(
        d.Source,
        Text(d.Body["name"]),
        Text(d.Body["reason"]),
        ToMatch(d.Body["match"]),
        Map(d.Body["set"]),
        Map(d.Body["default"]),
        Map(d.Body["add"]).ToDictionary(e => e.Key, e => ToNode(e.Value)));

    private static NamingRule ToNamingRule(JToken rule) => new(Text(rule["pattern"]), (int?)rule["maxLength"] ?? 0, Text(rule["allowed"]));

    private static MatchCriteria ToMatch(JToken? match) => new(Map(match).ToDictionary(e => e.Key, e => Text(e.Value)));

    private static Node ToNode(JToken node) => new(
        Text(node["template"]),
        Enum.Parse<NodeKind>((string?)node["kind"] ?? nameof(NodeKind.Create), ignoreCase: true),
        Map(node["config"]));

    private static string Text(JToken? token) => (string?)token ?? string.Empty;

    private static List<string> Strings(JToken? token) => token is JArray array ? array.Select(Text).ToList() : [];

    private static Dictionary<string, JToken> Map(JToken? token) =>
        token is JObject obj ? obj.Properties().ToDictionary(p => p.Name, p => p.Value) : [];
}
