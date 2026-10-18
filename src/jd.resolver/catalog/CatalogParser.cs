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
    private const string ComponentKind = "Component";
    private const string RuntimeKind = "Runtime";
    private const string PolicyKind = "Policy";
    private const string NamingKind = "Naming";
    private const string RolesKind = "Roles";

    private static readonly string[] Kinds = [CatalogKind, ComponentKind, RuntimeKind, PolicyKind, NamingKind, RolesKind];

    // Kinds of earlier catalogs, and what replaced them (docs/architecture/components.md section 2).
    private static readonly Dictionary<string, string> ReplacedKinds = new()
    {
        ["ResourceType"] = $"a {ComponentKind} file holds the type's contract (exports) beside its implementations",
        ["Mapping"] = $"a {ComponentKind}'s implementations (per environment type, then class) replace a type's mappings, and a {RuntimeKind} file the runtime mapping",
    };

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
        var componentDocuments = OfKind(documents, ComponentKind);
        var runtimeDocuments = OfKind(documents, RuntimeKind);
        var policyDocuments = OfKind(documents, PolicyKind);
        var components = componentDocuments.Where(d => d.Valid).Select(ToComponent).ToList();
        var runtimes = runtimeDocuments.Where(d => d.Valid).Select(ToRuntime).ToList();
        var policies = policyDocuments.Where(d => d.Valid).Select(ToPolicy).ToList();
        var namingDocuments = OfKind(documents, NamingKind).Where(d => d.Valid).ToList();
        var naming = Merge(namingDocuments, "rules", ToNamingRule, errors);
        CheckNamingAllowed(namingDocuments, errors);
        var roles = Merge(OfKind(documents, RolesKind).Where(d => d.Valid), "roles", role => (string?)role ?? string.Empty, errors);

        // A file whose kind could not be read might have been the missing Catalog, so do not claim it is missing.
        CheckSingleCatalog(catalogs, unreadable == 0, errors);
        // Components and runtimes share one namespace: a requirement's type names one, an environment will offer either.
        CheckUnique([.. componentDocuments, .. runtimeDocuments], errors);
        CheckUnique(policyDocuments, errors);
        // A missing or invalid Catalog is reported on its own; checking against its types too would only add noise.
        var environmentTypes = catalogs.FirstOrDefault() is { Valid: true } first
            ? Map(first.Body["environmentTypes"]).ToDictionary(e => e.Key, e => new EnvironmentType((bool?)e.Value["approval"] ?? false))
            : null;
        CheckEnvironmentTypes(components, runtimes, policies, environmentTypes, errors);
        CheckPolicyScopes(policies, errors);
        CheckPolicyPaths(policies, errors);
        CheckNodeNames(components, runtimes, policies, errors);
        CheckRuntimeNode(components, policies, errors);
        CheckReleases(runtimes, errors);
        CheckSameClasses(components, errors);
        CheckExportsContract(components, errors);

        // No errors means exactly one valid Catalog file, so its environment types were read.
        if (errors.Count > 0 || environmentTypes is null)
        {
            return new CatalogLoadResult(null, errors);
        }

        var version = (string?)catalogs[0].Body["version"] ?? string.Empty;
        var catalog = new Catalog(
            version,
            environmentTypes,
            components.ToDictionary(c => c.Name),
            runtimes.ToDictionary(r => r.Name),
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
            errors.Add(new LoadError(source.Path, "kind", ReplacedKinds.TryGetValue(kind, out var replacement)
                ? $"kind '{kind}' is no longer supported: {replacement}."
                : $"unknown kind '{kind}'; expected one of {string.Join(", ", Kinds)}."));
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

    private static void CheckUnique(List<Document> documents, List<LoadError> errors)
    {
        foreach (var group in documents.Where(d => NameOf(d) is not null).GroupBy(d => NameOf(d)))
        {
            foreach (var duplicate in group.Skip(1))
            {
                errors.Add(new LoadError(duplicate.Source, "name", $"'{group.Key}' is already declared in {group.First().Source}."));
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

    // A runtime is deployed in steps (dark revision, grants, probe, traffic shift), and the engine owns the inputs its release block
    // names, so the runtime node must not set them. The schema requires the probe, the release block and the runtime node.
    private static void CheckReleases(List<Runtime> runtimes, List<LoadError> errors)
    {
        foreach (var runtime in runtimes)
        {
            foreach (var (key, implementation) in runtime.Implementations)
            {
                if (implementation.Probe.IntervalSeconds > implementation.Probe.TimeoutSeconds)
                {
                    errors.Add(new LoadError(runtime.Source, $"implementations.{key}.probe.intervalSeconds", "must not exceed timeoutSeconds."));
                }

                var node = implementation.Nodes[ExpressionEvaluator.RuntimeNode];
                foreach (var input in new[] { implementation.Release.SuffixInput, implementation.Release.TrafficInput }.Where(node.Config.ContainsKey))
                {
                    errors.Add(new LoadError(runtime.Source, $"implementations.{key}.nodes.{ExpressionEvaluator.RuntimeNode}.config.{input}", "the release flow sets this input; the runtime node must not."));
                }
            }
        }
    }

    // Node names become parts of graph node ids and backend stack names, so they are lowercase kebab. Enforced here, not in the
    // schema: the schema validator ignores the propertyNames keyword.
    private static void CheckNodeNames(List<Component> components, List<Runtime> runtimes, List<Policy> policies, List<LoadError> errors)
    {
        var named = components.SelectMany(c => ClassParts(c).Select(p => (c.Source, Field: $"{p.Path}.nodes", Names: p.Part.Nodes.Keys)))
            .Concat(runtimes.SelectMany(r => r.Implementations.Select(i => (r.Source, Field: $"implementations.{i.Key}.nodes", Names: i.Value.Nodes.Keys))))
            .Concat(policies.Select(p => (p.Source, Field: "add", Names: p.Add.Keys)));
        foreach (var (source, field, names) in named)
        {
            foreach (var name in names.Where(n => !NodeNamePattern.IsMatch(n)))
            {
                errors.Add(new LoadError(source, $"{field}.{name}", $"'{name}' is not a valid node name; use lowercase letters, digits and hyphens, starting with a letter and not ending in a hyphen."));
            }
        }
    }

    // The node named 'runtime' is the workload's runtime: only a Runtime declares it (its schema requires it), since every
    // ${runtime.<output>} points to it.
    private static void CheckRuntimeNode(List<Component> components, List<Policy> policies, List<LoadError> errors)
    {
        const string reserved = ExpressionEvaluator.RuntimeNode;
        foreach (var component in components)
        {
            foreach (var (path, _) in ClassParts(component).Where(p => p.Part.Nodes.ContainsKey(reserved)))
            {
                errors.Add(new LoadError(component.Source, $"{path}.nodes.{reserved}", $"'{reserved}' is the runtime node, declared only by a {RuntimeKind}."));
            }
        }

        foreach (var policy in policies.Where(p => p.Add.ContainsKey(reserved)))
        {
            errors.Add(new LoadError(policy.Source, $"add.{reserved}", $"'{reserved}' is the runtime node, declared only by a {RuntimeKind}."));
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
                    Fail("match.runtime", $"'runtime' needs 'kind: {MatchCriteria.RuntimeKind}'; node-scope policies match type, class, environmentType and template.");
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

    // A policy match or an implementation keyed by a type nobody declared would load and never apply.
    private static void CheckEnvironmentTypes(
        List<Component> components, List<Runtime> runtimes, List<Policy> policies, Dictionary<string, EnvironmentType>? environmentTypes, List<LoadError> errors)
    {
        if (environmentTypes is null)
        {
            return;
        }

        var keyed = components.SelectMany(c => c.Implementations.Keys.Select(k => (c.Source, Type: k)))
            .Concat(runtimes.SelectMany(r => r.Implementations.Keys.Select(k => (r.Source, Type: k))))
            .Where(i => i.Type != ImplementationKey.Default)
            .Select(i => (i.Source, Location: $"implementations.{i.Type}", i.Type));
        var matched = policies.Where(p => p.Match.Criteria.ContainsKey(MatchCriteria.EnvironmentTypeKey))
            .Select(p => (p.Source, Location: $"match.{MatchCriteria.EnvironmentTypeKey}", Type: p.Match.Criteria[MatchCriteria.EnvironmentTypeKey]));
        foreach (var (source, location, type) in keyed.Concat(matched).Where(e => !environmentTypes.ContainsKey(e.Type)))
        {
            errors.Add(new LoadError(source, location, $"'{type}' is not a declared environment type (declared: {string.Join(", ", environmentTypes.Keys.Order())})."));
        }
    }

    // A workload's class must be valid in every environment, so every implementation declares the same classes.
    private static void CheckSameClasses(List<Component> components, List<LoadError> errors)
    {
        foreach (var component in components)
        {
            var (firstKey, first) = component.Implementations.First();
            foreach (var (key, implementation) in component.Implementations.Where(i => !i.Value.Classes.Keys.ToHashSet().SetEquals(first.Classes.Keys)))
            {
                errors.Add(new LoadError(component.Source, $"implementations.{key}.classes",
                    $"declares {string.Join(", ", implementation.Classes.Keys)}; every implementation must declare the classes of '{firstKey}' ({string.Join(", ", first.Classes.Keys)})."));
            }
        }
    }

    // Every class part must export exactly the component's contract, so ${resource.<id>.<export>} resolves in every environment.
    private static void CheckExportsContract(List<Component> components, List<LoadError> errors)
    {
        foreach (var component in components)
        {
            foreach (var (path, part) in ClassParts(component))
            {
                // An export is evaluated in its own requirement's scope, where another requirement's exports are out of reach.
                foreach (var export in part.Exports.Where(e => ExpressionEvaluator.ReadsResource(e.Value)).Select(e => e.Key))
                {
                    errors.Add(new LoadError(component.Source, $"{path}.exports.{export}", "an export cannot reference ${resource.<id>.<export>}; it may read env.*, its own nodes' outputs and runtime.*."));
                }

                var missing = component.Exports.Where(e => !part.Exports.ContainsKey(e)).ToList();
                var extra = part.Exports.Keys.Where(e => !component.Exports.Contains(e)).ToList();
                if (missing.Count > 0 || extra.Count > 0)
                {
                    var problems = new[] { missing.Count > 0 ? $"missing {string.Join(", ", missing)}" : null, extra.Count > 0 ? $"not in the contract: {string.Join(", ", extra)}" : null };
                    errors.Add(new LoadError(component.Source, $"{path}.exports", $"must export exactly the contract of component '{component.Name}' ({string.Join(", ", component.Exports)}); {string.Join("; ", problems.OfType<string>())}."));
                }
            }
        }
    }

    private static IEnumerable<(string Path, Part Part)> ClassParts(Component component) =>
        component.Implementations.SelectMany(i => i.Value.Classes.Select(c => ($"implementations.{i.Key}.classes.{c.Key}", c.Value)));

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

    private static Component ToComponent(Document d) => new(
        d.Source,
        Text(d.Body["name"]),
        Strings(d.Body["exports"]),
        Map(d.Body["implementations"]).ToDictionary(i => i.Key, i => new Implementation(Map(i.Value["classes"]).ToDictionary(c => c.Key, c => ToPart(c.Value)))));

    private static Part ToPart(JToken part) => new(ToNodes(part["nodes"]), Map(part["exports"]).ToDictionary(e => e.Key, e => Text(e.Value)));

    private static Runtime ToRuntime(Document d) => new(
        d.Source,
        Text(d.Body["name"]),
        Map(d.Body["implementations"]).ToDictionary(i => i.Key, i => ToRuntimeImplementation(i.Value)));

    private static RuntimeImplementation ToRuntimeImplementation(JToken implementation)
    {
        // Safe: only schema-valid documents reach here, and runtime.schema.json requires `probe`, `release` and all four probe fields.
        var probe = implementation["probe"]!;
        var release = implementation["release"]!;
        return new RuntimeImplementation(
            new Probe(Text(probe["path"]), (int)probe["expectedStatus"]!, (int)probe["timeoutSeconds"]!, (int)probe["intervalSeconds"]!),
            new RuntimeRelease(
                Text(release["suffixInput"]), Text(release["trafficInput"]), Text(release["trafficOutput"]), Text(release["suffixOutput"]), Text(release["revisionOutput"]),
                Text(release["fqdnOutput"]), Text(release["revisionKey"]), Text(release["latestKey"]), Text(release["weightKey"]), Text(release["resourceIdOutput"])),
            ToNodes(implementation["nodes"]));
    }

    private static Policy ToPolicy(Document d) => new(
        d.Source,
        Text(d.Body["name"]),
        Text(d.Body["reason"]),
        ToMatch(d.Body["match"]),
        Map(d.Body["set"]),
        Map(d.Body["default"]),
        ToNodes(d.Body["add"]));

    private static NamingRule ToNamingRule(JToken rule) => new(Text(rule["pattern"]), (int?)rule["maxLength"] ?? 0, Text(rule["allowed"]));

    private static MatchCriteria ToMatch(JToken? match) => new(Map(match).ToDictionary(e => e.Key, e => Text(e.Value)));

    private static Dictionary<string, Node> ToNodes(JToken? nodes) => Map(nodes).ToDictionary(e => e.Key, e => ToNode(e.Value));

    private static Node ToNode(JToken node) => new(
        Text(node["template"]),
        Enum.Parse<NodeKind>((string?)node["kind"] ?? nameof(NodeKind.Create), ignoreCase: true),
        Map(node["config"]));

    private static string Text(JToken? token) => (string?)token ?? string.Empty;

    private static List<string> Strings(JToken? token) => token is JArray array ? array.Select(Text).ToList() : [];

    private static Dictionary<string, JToken> Map(JToken? token) =>
        token is JObject obj ? obj.Properties().ToDictionary(p => p.Name, p => p.Value) : [];
}
