using System.Text.RegularExpressions;
using jd.resolver.environment;
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
    // Bridge: the kinds the catalog dispatches on, until the binder reads catalog documents by their schemas (S24b1, S24b3).
    private const string CatalogKind = "Catalog";
    private const string ComponentKind = "Component";
    private const string RuntimeKind = "Runtime";
    private const string SharedResourceKind = "SharedResource";
    private const string PolicyKind = "Policy";
    private const string NamingKind = "Naming";
    private const string RolesKind = "Roles";
    private const string GrantsKind = "Grants";

    private static readonly string[] Kinds = [CatalogKind, ComponentKind, RuntimeKind, SharedResourceKind, PolicyKind, NamingKind, RolesKind, GrantsKind];

    // Kinds of earlier catalogs, and what replaced them (docs/architecture/components.md section 2).
    private static readonly Dictionary<string, string> ReplacedKinds = new()
    {
        ["ResourceType"] = $"a {ComponentKind} file holds the type's contract (exports) beside its implementations",
        ["Mapping"] = $"a {ComponentKind}'s implementations (per environment type, then class) replace a type's mappings, and a {RuntimeKind} file the runtime mapping",
    };

    // A policy field of earlier catalogs: a policy requires, it never adds (docs/architecture/components.md section 3).
    private const string RemovedPolicyAdd = "add";

    private static readonly Regex NodeNamePattern = new("^[a-z]([a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant);

    // One part, with what its config and exports may read besides its own nodes and the reserved roots: the shared resources in Uses and,
    // for a class part, its own component (OwnComponent) and that implementation's environment part. Prefix locates the part in its file;
    // Scope is null for the workload's parts, so a runtime's part is the workload part without an own component.
    private sealed record Reader(
        string Source, string Prefix, string Owner, SharedScope? Scope, Part Part, IReadOnlyList<string> Uses, string? OwnComponent, Part? OwnEnvironment)
    {
        public bool IsRuntime => Scope is null && OwnComponent is null;
    }

    /// <summary>Builds the catalog from its files; the value is set exactly when no file has an error.</summary>
    public static async Task<LoadResult<Catalog>> ParseAsync(IEnumerable<CatalogSource> sources, CancellationToken cancellationToken = default)
    {
        var errors = new List<LoadError>();
        var documents = new List<Document>();
        // A document that failed its schema is not in here, but still counts toward the cross-file checks that only need its kind or name,
        // so one mistake is not reported a second time as a missing or undeclared item.
        var valid = new HashSet<Document>();
        var unreadable = 0;
        foreach (var source in sources.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            if (await ReadDocumentAsync(source, errors, cancellationToken) is not (var document, var isValid))
            {
                unreadable++;
                continue;
            }

            documents.Add(document);
            if (isValid)
            {
                valid.Add(document);
            }
        }

        var catalogs = OfKind(documents, CatalogKind);
        var componentDocuments = OfKind(documents, ComponentKind);
        var runtimeDocuments = OfKind(documents, RuntimeKind);
        var sharedDocuments = OfKind(documents, SharedResourceKind);
        var policyDocuments = OfKind(documents, PolicyKind);
        var components = componentDocuments.Where(valid.Contains).Select(ToComponent).ToList();
        var runtimes = runtimeDocuments.Where(valid.Contains).Select(ToRuntime).ToList();
        var shared = sharedDocuments.Where(valid.Contains).Select(ToSharedResource).ToList();
        var policies = policyDocuments.Where(valid.Contains).Select(ToPolicy).ToList();
        var namingDocuments = OfKind(documents, NamingKind).Where(valid.Contains).ToList();
        var naming = Merge(namingDocuments, "rules", ToNamingRule, errors);
        CheckNamingAllowed(namingDocuments, errors);
        var roles = Merge(OfKind(documents, RolesKind).Where(valid.Contains), "roles", role => (string?)role ?? string.Empty, errors);
        var grantTargets = Merge(OfKind(documents, GrantsKind).Where(valid.Contains), "targets", Text, errors);

        // A file whose kind could not be read might have been the missing Catalog, so do not claim it is missing.
        CheckSingleCatalog(catalogs, unreadable == 0, errors);
        // Shared resources, components and runtimes share one namespace: each is a root a part reads (${<name>.<export>}), a
        // requirement's type names a component, an environment will offer components and a runtime.
        // Files that failed their schema still count as declared, so their mistake is not reported again at each use.
        var named = documents.Where(d => d.Kind is ComponentKind or RuntimeKind or SharedResourceKind).ToList();
        CheckUnique(named, errors);
        CheckReservedNames(named, errors);
        var kinds = named.SelectMany(d => NameOf(d) is { } name ? [(Name: name, d.Kind)] : Array.Empty<(string Name, string Kind)>())
            .DistinctBy(n => n.Name).ToDictionary(n => n.Name, n => n.Kind);
        // A duplicate name is reported by CheckUnique; the checks below read the first.
        var sharedByName = shared.DistinctBy(s => s.Name).ToDictionary(s => s.Name);
        CheckUses(sharedByName, components, runtimes, kinds, errors);
        var readers = Readers(shared, components, runtimes);
        CheckNodeNames(readers, errors);
        CheckReads(readers, sharedByName, kinds, errors);
        CheckScopeNodeNames(readers, sharedByName.Keys, errors);
        CheckGrants(readers, grantTargets, errors);
        CheckUnique(policyDocuments, errors);
        // A missing or invalid Catalog is reported on its own; checking against its types too would only add noise.
        var environmentTypes = catalogs.FirstOrDefault() is { } first && valid.Contains(first)
            ? Map(first.Body["environmentTypes"]).ToDictionary(e => e.Key, e => new EnvironmentType((bool?)e.Value["approval"] ?? false, Enum.Parse<SharedScope>(Text(e.Value["layer"]), ignoreCase: true)))
            : null;
        CheckEnvironmentTypes(components, runtimes, policies, environmentTypes, errors);
        CheckPolicyScopes(policies, errors);
        CheckPolicyRequires(policies, componentDocuments, errors);
        CheckPolicyPaths(policies, errors);
        CheckReleases(runtimes, errors);
        CheckSameClasses(components, errors);
        CheckExportsContract(components, errors);

        // No errors means exactly one valid Catalog file, so its environment types were read.
        if (errors.Count > 0 || environmentTypes is null)
        {
            return new LoadResult<Catalog>(null, errors);
        }

        var version = (string?)catalogs[0].Body["version"] ?? string.Empty;
        var catalog = new Catalog(
            version,
            environmentTypes,
            components.ToDictionary(c => c.Name),
            sharedByName,
            runtimes.ToDictionary(r => r.Name),
            grantTargets,
            policies,
            naming,
            roles);
        return new LoadResult<Catalog>(catalog, errors);
    }

    // The document and whether it passed its schema; null when the file has no readable kind.
    private static async Task<(Document Document, bool Valid)?> ReadDocumentAsync(CatalogSource source, List<LoadError> errors, CancellationToken cancellationToken)
    {
        var parsed = DocumentLoader.Parse(source.Path, source.Content, Kinds);
        errors.AddRange(parsed.Errors);
        if (parsed.Value is not { } document)
        {
            return null;
        }

        if (ReplacedKinds.TryGetValue(document.Kind, out var replacement))
        {
            errors.Add(new LoadError(source.Path, DocumentLoader.KindField, $"kind '{document.Kind}' is no longer supported: {replacement}."));
            return (document, false);
        }

        if (document.Kind == PolicyKind && document.Body.ContainsKey(RemovedPolicyAdd))
        {
            // One clear error instead of the schema's "not allowed"; the rest of the file is not checked until it is migrated.
            errors.Add(new LoadError(source.Path, RemovedPolicyAdd,
                "a policy can no longer add nodes: a workload declares what it gets in its 'requires'; use 'require: <component>' to make every workload the policy matches require one."));
            return (document, false);
        }

        // An unknown kind is refused here: readable, so not a missing Catalog, and no kind-specific check applies to it.
        var schemaErrors = await DocumentLoader.ValidateAsync(document, Kinds, cancellationToken);
        errors.AddRange(schemaErrors);
        return (document, schemaErrors.Count == 0);
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
            errors.Add(new LoadError(extra.Source, DocumentLoader.KindField, $"a second {CatalogKind} file; exactly one is allowed (first: {catalogs[0].Source})."));
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

    // A name equal to a reserved root could never be read: ${env.x} always means the environment.
    private static void CheckReservedNames(List<Document> named, List<LoadError> errors)
    {
        foreach (var document in named.Where(d => NameOf(d) is { } name && ExpressionEvaluator.ReservedRoots.Contains(name)))
        {
            errors.Add(new LoadError(document.Source, "name", $"'{NameOf(document)}' is a reserved root of expressions ({string.Join(", ", ExpressionEvaluator.ReservedRoots.Order())}); choose another name."));
        }
    }

    // uses names shared resources only (a component's parts are read through its contract), in a direction their deploy order allows:
    // the shared layer is deployed before any environment, so a shared-scope resource cannot read an environment-scope one. No cycles,
    // so reading a used resource's exports always ends.
    private static void CheckUses(
        Dictionary<string, SharedResource> shared, List<Component> components, List<Runtime> runtimes, Dictionary<string, string> kinds, List<LoadError> errors)
    {
        var declared = kinds.Where(k => k.Value == SharedResourceKind).Select(k => k.Key).Order(StringComparer.Ordinal).ToList();
        var users = shared.Values.Select(s => (s.Source, s.Uses, Scope: (SharedScope?)s.Scope))
            .Concat(components.Select(c => (c.Source, c.Uses, Scope: (SharedScope?)null)))
            .Concat(runtimes.Select(r => (r.Source, r.Uses, Scope: (SharedScope?)null)));
        foreach (var (source, uses, scope) in users)
        {
            foreach (var used in uses)
            {
                var problem = kinds.GetValueOrDefault(used) switch
                {
                    SharedResourceKind => scope == SharedScope.Shared && shared.GetValueOrDefault(used)?.Scope == SharedScope.Environment
                        ? $"'{used}' is environment scope; a shared-scope resource uses only shared-scope ones, since the shared layer is deployed first."
                        : null,
                    null => $"'{used}' is not a shared resource in the catalog (shared resources: {string.Join(", ", declared)}).",
                    _ => $"'{used}' is a component or runtime; uses lists shared resources only.",
                };
                if (problem is not null)
                {
                    errors.Add(new LoadError(source, "uses", problem));
                }
            }
        }

        // A name in uses that is no (valid) shared resource is reported above, so the order runs over the shared resources only.
        var (_, cycle) = TopologicalOrder.Sort(shared.ToDictionary(s => s.Key, s => (IReadOnlyCollection<string>)s.Value.Uses.Where(shared.ContainsKey).ToList()));
        if (cycle is not null)
        {
            errors.Add(new LoadError(shared[cycle[0]].Source, "uses", $"uses cycle: {string.Join(" -> ", cycle)}."));
        }
    }

    private static List<Reader> Readers(List<SharedResource> shared, List<Component> components, List<Runtime> runtimes) =>
    [
        .. shared.Select(s => new Reader(s.Source, string.Empty, s.Name, s.Scope, s.Part, s.Uses, OwnComponent: null, OwnEnvironment: null)),
        .. components.SelectMany(c => c.Implementations.SelectMany(i => i.Value.Environment is { } environment
            ? [new Reader(c.Source, $"implementations.{i.Key}.environment.", c.Name, SharedScope.Environment, environment, c.Uses, OwnComponent: null, OwnEnvironment: null)]
            : Array.Empty<Reader>())),
        .. components.SelectMany(c => ClassParts(c).Select(p => new Reader(c.Source, $"{p.Path}.", c.Name, Scope: null, p.Part, c.Uses, c.Name, p.Environment))),
        .. runtimes.SelectMany(r => r.Implementations.Select(i => new Reader(
            r.Source, $"implementations.{i.Key}.", r.Name, Scope: null, new Part(i.Value.Nodes, new Dictionary<string, string>()), r.Uses, OwnComponent: null, OwnEnvironment: null))),
    ];

    // Node names become parts of graph node ids and backend stack names, so they are lowercase kebab (enforced here: the schema validator
    // ignores the propertyNames keyword). A node may not take a name its part reads as a root, or one of the two would be unreachable;
    // the runtime's own 'runtime' node is the reserved root.
    private static void CheckNodeNames(List<Reader> readers, List<LoadError> errors)
    {
        foreach (var reader in readers)
        {
            foreach (var node in reader.Part.Nodes.Keys)
            {
                var problem =
                    !NodeNamePattern.IsMatch(node) ? $"'{node}' is not a valid node name; use lowercase letters, digits and hyphens, starting with a letter and not ending in a hyphen."
                    : node == ExpressionEvaluator.RuntimeNode ? (reader.IsRuntime ? null : $"'{node}' is the runtime node, declared only by a {RuntimeKind}.")
                    : ExpressionEvaluator.ReservedRoots.Contains(node) || reader.Uses.Contains(node) || node == reader.OwnComponent ? $"'{node}' is a name this part reads as a root; rename the node."
                    : null;
                if (problem is not null)
                {
                    errors.Add(new LoadError(reader.Source, $"{reader.Prefix}nodes.{node}", problem));
                }
            }
        }
    }

    // Every root a part reads is one of its own nodes, a reserved root, a shared resource in its uses or (a class part) its own
    // component, and <name>.<export> names a declared export.
    private static void CheckReads(List<Reader> readers, Dictionary<string, SharedResource> shared, Dictionary<string, string> kinds, List<LoadError> errors)
    {
        foreach (var reader in readers)
        {
            var texts = reader.Part.Nodes.Values.SelectMany(n => n.Config.Values.SelectMany(v => v is JContainer container ? container.DescendantsAndSelf() : [v]))
                .OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (Location: v.Path, Text: Text(v)))
                .Concat(reader.Part.Exports.Select(e => (Location: $"{reader.Prefix}exports.{e.Key}", Text: e.Value)));
            foreach (var (location, text) in texts)
            {
                foreach (var path in ExpressionEvaluator.Paths(text))
                {
                    var segments = path.Split('.');
                    var root = segments[0];
                    var kind = kinds.GetValueOrDefault(root);
                    var exports = reader.Uses.Contains(root) ? shared.GetValueOrDefault(root)?.Part.Exports : root == reader.OwnComponent ? reader.OwnEnvironment?.Exports : null;
                    var problem =
                        root == ExpressionEvaluator.EnvNamespace && !(segments.Length == 2 && EnvironmentDescriptor.ReservedKeys.Contains(segments[1]))
                            ? ExpressionEvaluator.EnvLimit(path)
                        : ExpressionEvaluator.ReservedRoots.Contains(root) || reader.Part.Nodes.ContainsKey(root) ? null
                        : reader.Uses.Contains(root) && exports is null ? null // its file failed its schema, or uses names no shared resource: reported on its own
                        : exports is not null ? (segments.Length == 2 && exports.ContainsKey(segments[1]) ? null : $"'{path}': '{root}' exports {string.Join(", ", exports.Keys)}.")
                        : root == reader.OwnComponent ? $"'{path}': component '{root}' has no environment part in this implementation."
                        : root == reader.Owner && kind == ComponentKind ? $"'{path}': an environment part cannot read its own component."
                        : kind is ComponentKind or RuntimeKind ? $"'{path}' reads another component or runtime; a part reads only its own nodes, what it uses and, in a class, its own component."
                        : kind == SharedResourceKind ? $"'{path}' reads '{root}', which is not in uses; add it to the file's uses."
                        : $"'{path}' reads unknown root '{root}'; expected a node of this part ({string.Join(", ", reader.Part.Nodes.Keys)}), a name in uses, {(reader.OwnComponent is null ? string.Empty : $"'{reader.OwnComponent}', ")}or one of {string.Join(", ", ExpressionEvaluator.ReservedRoots.Order())}.";
                    if (problem is not null)
                    {
                        errors.Add(new LoadError(reader.Source, location, problem));
                    }
                }
            }
        }
    }

    // The parts of one shared or environment run expand into one scope (docs/architecture/components.md section 3), so their node
    // names are unique across the parts that can share it; a component's implementations may reuse a name, since one is selected.
    // A node may not take a shared resource's name either: a run looks up nodes before parts, so it would silently replace what
    // ${<resource>.<export>} reads (components.md section 2); a name the part itself uses is refused by CheckNodeNames.
    private static void CheckScopeNodeNames(List<Reader> readers, IEnumerable<string> sharedNames, List<LoadError> errors)
    {
        var nodes = readers.Where(r => r.Scope is not null).SelectMany(r => r.Part.Nodes.Keys.Select(n => (Reader: r, Node: n))).ToList();
        var resources = sharedNames.ToHashSet();
        foreach (var (reader, node) in nodes.Where(n => resources.Contains(n.Node) && !n.Reader.Uses.Contains(n.Node)))
        {
            errors.Add(new LoadError(reader.Source, $"{reader.Prefix}nodes.{node}", $"node '{node}' takes the name of shared resource '{node}', which a part of the same run reads as ${{{node}.<export>}}."));
        }

        foreach (var group in nodes.GroupBy(n => (n.Reader.Scope, n.Node)))
        {
            var first = group.First().Reader;
            foreach (var (reader, node) in group.Where(n => n.Reader.Owner != first.Owner))
            {
                errors.Add(new LoadError(reader.Source, $"{reader.Prefix}nodes.{node}",
                    $"node '{node}' is also declared by '{first.Owner}' ({first.Source}); node names are unique across the parts of one {$"{group.Key.Scope}".ToLowerInvariant()} scope."));
            }
        }
    }

    // A grant's target may only be the workload's own resource (security boundaries 1 and 2): a grant node sits in a class part or the
    // runtime, its template is listed in grants.yaml, and the input that names its target is exactly ${<node>.<output>} of a node of the
    // same part or the runtime. Its other inputs may read what the component uses. The graph builder checks the target again after policies.
    private static void CheckGrants(List<Reader> readers, IReadOnlyDictionary<string, string> targets, List<LoadError> errors)
    {
        foreach (var reader in readers)
        {
            foreach (var (name, node) in reader.Part.Nodes.Where(n => n.Value.Kind == NodeKind.Grant))
            {
                var location = $"{reader.Prefix}nodes.{name}";
                if (reader.Scope is not null)
                {
                    errors.Add(new LoadError(reader.Source, location, $"a grant node belongs only in a class part or the runtime, not in a part of the {$"{reader.Scope}".ToLowerInvariant()} scope: a grant targets only the workload's own resources."));
                }
                else if (!targets.TryGetValue(node.Template, out var input))
                {
                    errors.Add(new LoadError(reader.Source, $"{location}.template", $"'{node.Template}' is not a grant template in the catalog's {GrantsKind} targets ({string.Join(", ", targets.Keys.Order(StringComparer.Ordinal))}); list it with the input that names its target."));
                }
                else if (!(node.Config.GetValueOrDefault(input) is JValue { Type: JTokenType.String } target
                    && ExpressionEvaluator.SingleReference(Text(target)) is { } reference
                    && (reader.Part.Nodes.ContainsKey(reference.Node) || reference.Node == ExpressionEvaluator.RuntimeNode)))
                {
                    errors.Add(new LoadError(reader.Source, $"{location}.config.{input}", ExpressionEvaluator.GrantTargetRule(input)));
                }
            }
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

                if (policy.Require is not null)
                {
                    Fail("require", $"a node-scope policy cannot require a component; use 'kind: {MatchCriteria.RuntimeKind}' to make every workload require it.");
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

    // A required component must exist, or no workload could satisfy the policy. A runtime is never required: every workload gets it.
    // Component files that failed their schema still count, so their mistake is not reported a second time here.
    private static void CheckPolicyRequires(List<Policy> policies, List<Document> componentDocuments, List<LoadError> errors)
    {
        var declared = componentDocuments.Select(NameOf).OfType<string>().ToHashSet();
        foreach (var policy in policies.Where(p => p.Require is not null && !declared.Contains(p.Require)))
        {
            errors.Add(new LoadError(policy.Source, "require", $"'{policy.Require}' is not a component in the catalog (components: {string.Join(", ", declared.Order(StringComparer.Ordinal))})."));
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

        // A shared-layer environment deploys no component part, so an implementation for its type would never be used.
        foreach (var (source, location, type) in keyed.Where(e => environmentTypes.GetValueOrDefault(e.Type)?.Layer == SharedScope.Shared))
        {
            errors.Add(new LoadError(source, location, $"'{type}' is a shared-layer environment type; implementations are keyed by environment-layer types or '{ImplementationKey.Default}'."));
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
            foreach (var (path, part, _) in ClassParts(component))
            {
                // An export is evaluated in its own requirement's scope, where another requirement's exports are out of reach.
                foreach (var export in part.Exports.Where(e => ExpressionEvaluator.ReadsResource(e.Value)).Select(e => e.Key))
                {
                    errors.Add(new LoadError(component.Source, $"{path}.exports.{export}", "an export cannot reference ${resource.<id>.<export>}; it may read its own nodes' outputs, what its component uses, env.* and runtime.*."));
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

    // Each class part with where it is in the file and its implementation's environment part.
    private static IEnumerable<(string Path, Part Part, Part? Environment)> ClassParts(Component component) =>
        component.Implementations.SelectMany(i => i.Value.Classes.Select(c => ($"implementations.{i.Key}.classes.{c.Key}", c.Value, i.Value.Environment)));

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
        Strings(d.Body["uses"]),
        Map(d.Body["implementations"]).ToDictionary(i => i.Key, i => new Implementation(
            i.Value["environment"] is { } environment ? ToPart(environment) : null,
            Map(i.Value["classes"]).ToDictionary(c => c.Key, c => ToPart(c.Value)))));

    private static SharedResource ToSharedResource(Document d) => new(
        d.Source, Text(d.Body["name"]), Enum.Parse<SharedScope>(Text(d.Body["scope"]), ignoreCase: true), Strings(d.Body["uses"]), ToPart(d.Body));

    private static Part ToPart(JToken part) => new(ToNodes(part["nodes"]), Map(part["exports"]).ToDictionary(e => e.Key, e => Text(e.Value)));

    private static Runtime ToRuntime(Document d) => new(
        d.Source,
        Text(d.Body["name"]),
        Strings(d.Body["uses"]),
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
        (string?)d.Body["require"]);

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
