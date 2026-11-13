using System.Text.RegularExpressions;
using jd.resolver.environment;
using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>
/// Builds a <see cref="Catalog"/> from a <see cref="SourceCatalog"/>, whose templates are its own part (<see cref="Templates"/>). Pure: reading
/// the files is the client's job. Every problem in every file is collected, so a catalog can be
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

    // What the name rules call the catalog in errors about it as a whole.
    private const string SetName = "catalog";

    // One part of an owner of Kind, with what its config and exports may read besides its own nodes and the reserved roots: the shared
    // resources it reads and, for a class part, its own component (OwnComponent) and that implementation's environment part (OwnEnvironment).
    // Layer is the part's scope when that is a layer, null for the workload's parts.
    private sealed record Reader(string Kind, PartOwner Owner, Part Part, string? Layer, Part? OwnEnvironment)
    {
        public string Source => Owner.Source;

        public bool IsRuntime => Kind == RuntimeKind;

        public string? OwnComponent => Kind == ComponentKind && Layer is null ? Owner.Name : null;
    }

    // A runtime part's probe and release block, read where the part is (S24e binds them).
    private sealed record Rollout(PartOwner Runtime, Part Part, Probe Probe, RuntimeRelease Release);

    /// <summary>Templates are Pulumi YAML, not catalog documents: the layout keeps them in this directory of the catalog, a part of their own.</summary>
    public const string TemplatesDirectory = "templates";

    private const string TemplatesPrefix = TemplatesDirectory + "/";

    /// <summary>The templates part of <paramref name="catalog"/>: its documents under <see cref="TemplatesDirectory"/>, named relative to it, under the name the catalog's name and the directory make.</summary>
    public static SourceCatalog Templates(SourceCatalog catalog) => new(
        Path.Combine(catalog.Name, TemplatesDirectory),
        [.. catalog.Documents.Where(IsTemplate).Select(document => document with { Name = document.Name[TemplatesPrefix.Length..] })]);

    /// <summary>Builds the catalog from its documents, the templates part aside; the value is set exactly when no document has an error.</summary>
    public static async Task<LoadResult<Catalog>> ParseAsync(SourceCatalog source, CancellationToken cancellationToken = default)
    {
        var errors = new List<LoadError>();
        var documents = new List<Document>();
        // A document that failed its schema is not in here, but still counts toward the cross-file checks that only need its kind or name,
        // so one mistake is not reported a second time as a missing or undeclared item.
        var valid = new HashSet<Document>();
        var unreadable = 0;
        foreach (var file in source.Documents.Where(document => !IsTemplate(document)).OrderBy(document => document.Name, StringComparer.Ordinal))
        {
            if (await ReadDocumentAsync(file, errors, cancellationToken) is not (var document, var isValid))
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

        // Names first (one Catalog, names unique, references declared); the checks below read the first of a duplicate name.
        var index = await SchemaIndex.Shipped;
        errors.AddRange(NameRules.Check(index, new DocumentSet(SetName, Kinds, documents, valid, unreadable)));
        var layers = index.Layers;
        var binder = new Binder(index);
        var catalogs = OfKind(documents, CatalogKind);
        var components = Owners(binder, documents, ComponentKind, valid);
        var boundRuntimes = Bound(binder, documents, RuntimeKind, valid);
        var runtimes = boundRuntimes.Select(runtime => runtime.Owner).ToList();
        var rollouts = boundRuntimes.SelectMany(runtime => runtime.Owner.Parts.Select(part => ToRollout(runtime.Document, runtime.Owner, part))).ToList();
        var shared = Owners(binder, documents, SharedResourceKind, valid);
        var policies = OfKind(documents, PolicyKind).Where(valid.Contains).Select(ToPolicy).ToList();
        var namingDocuments = OfKind(documents, NamingKind).Where(valid.Contains).ToList();
        var naming = Merge(namingDocuments, "rules", ToNamingRule);
        CheckNamingAllowed(namingDocuments, errors);
        var roles = Merge(OfKind(documents, RolesKind).Where(valid.Contains), "roles", role => (string?)role ?? string.Empty);
        var grantTargets = Merge(OfKind(documents, GrantsKind).Where(valid.Contains), "targets", Text);

        // Shared resources, components and runtimes share one namespace: each is a root a part reads (${<name>.<export>}), a
        // requirement's type names a component, an environment will offer components and a runtime.
        // Files that failed their schema still count as declared, so their mistake is not reported again at each use.
        var named = documents.Where(d => d.Kind is ComponentKind or RuntimeKind or SharedResourceKind).ToList();
        CheckReservedNames(named, errors);
        var kinds = named.SelectMany(d => NameOf(d) is { } name ? [(Name: name, d.Kind)] : Array.Empty<(string Name, string Kind)>())
            .DistinctBy(n => n.Name).ToDictionary(n => n.Name, n => n.Kind);
        var sharedByName = shared.DistinctBy(s => s.Name).ToDictionary(s => s.Name);
        CheckUses(sharedByName, layers, errors);
        var readers = Readers(layers, shared, components, runtimes);
        CheckNodeNames(readers, errors);
        CheckReads(readers, sharedByName.ToDictionary(s => s.Key, s => Only(s.Value).Exports), kinds, errors);
        CheckScopeNodeNames(readers, sharedByName.Keys, errors);
        CheckGrants(readers, grantTargets, errors);
        // A missing or invalid Catalog is reported on its own; checking against its types too would only add noise.
        var environmentTypes = catalogs.FirstOrDefault() is { } first && valid.Contains(first)
            ? Map(first.Body["environmentTypes"]).ToDictionary(e => e.Key, e => new EnvironmentType((bool?)e.Value["approval"] ?? false, Text(e.Value["layer"])))
            : null;
        CheckSharedLayerKeys(components.Concat(runtimes), environmentTypes, layers, errors);
        CheckPolicyScopes(policies, errors);
        CheckPolicyPaths(policies, errors);
        CheckReleases(rollouts, errors);
        CheckSameClasses(components, errors);
        CheckExportsContract(components, layers, errors);

        // No errors means exactly one valid Catalog file, so its environment types were read.
        if (errors.Count > 0 || environmentTypes is null)
        {
            return new LoadResult<Catalog>(null, errors);
        }

        var version = (string?)catalogs[0].Body["version"] ?? string.Empty;
        var catalog = new Catalog(
            version,
            layers,
            environmentTypes,
            components.ToDictionary(c => c.Name),
            sharedByName,
            runtimes.ToDictionary(r => r.Name),
            rollouts.ToDictionary(r => Catalog.RolloutKey(r.Runtime, r.Part), r => (r.Probe, r.Release)),
            grantTargets,
            policies,
            naming,
            roles);
        return new LoadResult<Catalog>(catalog, errors);
    }

    // The document and whether it passed its schema; null when the file has no readable kind.
    private static async Task<(Document Document, bool Valid)?> ReadDocumentAsync(SourceDocument source, List<LoadError> errors, CancellationToken cancellationToken)
    {
        var parsed = DocumentLoader.Parse(source.Name, source.Text(), Kinds);
        errors.AddRange(parsed.Errors);
        if (parsed.Value is not { } document)
        {
            return null;
        }

        // An unknown kind is refused here: readable, so not a missing Catalog, and no kind-specific check applies to it.
        var schemaErrors = await DocumentLoader.ValidateAsync(document, Kinds, cancellationToken);
        errors.AddRange(schemaErrors);
        return (document, schemaErrors.Count == 0);
    }

    private static bool IsTemplate(SourceDocument document) => document.Name.StartsWith(TemplatesPrefix, StringComparison.Ordinal);

    private static List<Document> OfKind(List<Document> documents, string kind) => documents.Where(d => d.Kind == kind).ToList();

    // A valid document has the name its schema requires.
    private static List<(Document Document, PartOwner Owner)> Bound(Binder binder, List<Document> documents, string kind, HashSet<Document> valid) =>
        [.. OfKind(documents, kind).Where(valid.Contains).Select(document => (document, binder.Owner(document, NameOf(document) ?? string.Empty)))];

    private static List<PartOwner> Owners(Binder binder, List<Document> documents, string kind, HashSet<Document> valid) =>
        [.. Bound(binder, documents, kind, valid).Select(bound => bound.Owner)];

    // A shared resource's document is its one part.
    private static Part Only(PartOwner shared) => shared.Parts[0];

    // Valid documents have the shapes the schemas guarantee; invalid ones are only asked for what they may lack.

    private static string? NameOf(Document d) => d.Body["name"] is { Type: JTokenType.String } name ? (string?)name : null;

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
    private static void CheckReleases(List<Rollout> rollouts, List<LoadError> errors)
    {
        foreach (var (runtime, part, probe, release) in rollouts)
        {
            if (probe.IntervalSeconds > probe.TimeoutSeconds)
            {
                errors.Add(new LoadError(runtime.Source, $"{part.Location}.probe.intervalSeconds", "must not exceed timeoutSeconds."));
            }

            var node = part.Nodes[ExpressionEvaluator.RuntimeNode];
            foreach (var input in new[] { release.SuffixInput, release.TrafficInput }.Where(node.Config.ContainsKey))
            {
                errors.Add(new LoadError(runtime.Source, $"{node.ConfigLocation}.{input}", "the release flow sets this input; the runtime node must not."));
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

    // uses names shared resources (the ref rule), in a direction their deploy order allows: a layer is deployed before the later ones, so a
    // resource cannot read one of a later layer; components and runtimes may use any. No cycles, so reading a used resource's exports always ends.
    private static void CheckUses(Dictionary<string, PartOwner> shared, IReadOnlyList<string> layers, List<LoadError> errors)
    {
        var order = layers.ToList();
        int Order(PartOwner resource) => order.IndexOf(Only(resource).Scope);
        foreach (var user in shared.Values)
        {
            foreach (var used in user.Reads.Select(shared.GetValueOrDefault).OfType<PartOwner>().Where(used => Order(used) > Order(user)))
            {
                var scope = Only(user).Scope;
                errors.Add(new LoadError(user.Source, "uses",
                    $"'{used.Name}' is {Only(used).Scope} scope; a {scope}-scope resource uses only {string.Join(" or ", layers.Take(Order(user) + 1))}-scope ones, since the {scope} layer is deployed first."));
            }
        }

        // A name in uses that is no (valid) shared resource is reported above, so the order runs over the shared resources only.
        var (_, cycle) = TopologicalOrder.Sort(shared.ToDictionary(s => s.Key, s => (IReadOnlyCollection<string>)s.Value.Reads.Where(shared.ContainsKey).ToList()));
        if (cycle is not null)
        {
            errors.Add(new LoadError(shared[cycle[0]].Source, "uses", $"uses cycle: {string.Join(" -> ", cycle)}."));
        }
    }

    // Every part, in the order its checks report: shared resources, the components' layer parts, their class parts, the runtimes.
    private static List<Reader> Readers(IReadOnlyList<string> layers, List<PartOwner> shared, List<PartOwner> components, List<PartOwner> runtimes)
    {
        Reader Read(string kind, PartOwner owner, Part part) => layers.Contains(part.Scope)
            ? new Reader(kind, owner, part, part.Scope, OwnEnvironment: null)
            : new Reader(kind, owner, part, Layer: null, owner.Parts.FirstOrDefault(other => layers.Contains(other.Scope) && part.Keys.Take(other.Keys.Count).SequenceEqual(other.Keys)));
        return
        [
            .. shared.Select(s => Read(SharedResourceKind, s, Only(s))),
            .. components.SelectMany(c => c.Parts.Where(p => layers.Contains(p.Scope)).Select(p => Read(ComponentKind, c, p))),
            .. components.SelectMany(c => c.Parts.Where(p => !layers.Contains(p.Scope)).Select(p => Read(ComponentKind, c, p))),
            .. runtimes.SelectMany(r => r.Parts.Select(p => Read(RuntimeKind, r, p))),
        ];
    }

    // A node may not take a name its part reads as a root, or one of the two would be unreachable; the runtime's own 'runtime' node is the
    // reserved root. The name's form is the part schema's.
    private static void CheckNodeNames(List<Reader> readers, List<LoadError> errors)
    {
        foreach (var reader in readers)
        {
            foreach (var (name, node) in reader.Part.Nodes)
            {
                var problem =
                    name == ExpressionEvaluator.RuntimeNode ? (reader.IsRuntime ? null : $"'{name}' is the runtime node, declared only by a {RuntimeKind}.")
                    : ExpressionEvaluator.ReservedRoots.Contains(name) || reader.Part.Reads.Contains(name) || name == reader.OwnComponent ? $"'{name}' is a name this part reads as a root; rename the node."
                    : null;
                if (problem is not null)
                {
                    errors.Add(new LoadError(reader.Source, node.Location, problem));
                }
            }
        }
    }

    // Every root a part reads is one of its own nodes, a reserved root, a shared resource in its uses or (a class part) its own
    // component, and <name>.<export> names a declared export.
    private static void CheckReads(
        List<Reader> readers, Dictionary<string, IReadOnlyDictionary<string, string>> sharedExports, Dictionary<string, string> kinds, List<LoadError> errors)
    {
        foreach (var reader in readers)
        {
            var texts = reader.Part.Nodes.Values.SelectMany(n => n.Config.Values.SelectMany(v => v is JContainer container ? container.DescendantsAndSelf() : [v]))
                .OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (Location: v.Path, Text: Text(v)))
                .Concat(reader.Part.Exports.Select(e => (Location: $"{reader.Part.ExportsLocation}.{e.Key}", Text: e.Value)));
            foreach (var (location, text) in texts)
            {
                foreach (var path in ExpressionEvaluator.Paths(text))
                {
                    var segments = path.Split('.');
                    var root = segments[0];
                    var kind = kinds.GetValueOrDefault(root);
                    var reads = reader.Part.Reads.Contains(root);
                    var exports = reads ? sharedExports.GetValueOrDefault(root) : root == reader.OwnComponent ? reader.OwnEnvironment?.Exports : null;
                    var problem =
                        root == ExpressionEvaluator.EnvNamespace && !(segments.Length == 2 && EnvironmentDescriptor.ReservedKeys.Contains(segments[1]))
                            ? ExpressionEvaluator.EnvLimit(path)
                        : ExpressionEvaluator.ReservedRoots.Contains(root) || reader.Part.Nodes.ContainsKey(root) ? null
                        : reads && exports is null ? null // its file failed its schema, or uses names no shared resource: reported on its own
                        : exports is not null ? (segments.Length == 2 && exports.ContainsKey(segments[1]) ? null : $"'{path}': '{root}' exports {string.Join(", ", exports.Keys)}.")
                        : root == reader.OwnComponent ? $"'{path}': component '{root}' has no environment part in this implementation."
                        : root == reader.Owner.Name && kind == ComponentKind ? $"'{path}': an environment part cannot read its own component."
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
        var nodes = readers.Where(r => r.Layer is not null).SelectMany(r => r.Part.Nodes.Select(n => (Reader: r, Name: n.Key, Node: n.Value))).ToList();
        var resources = sharedNames.ToHashSet();
        foreach (var (reader, name, node) in nodes.Where(n => resources.Contains(n.Name) && !n.Reader.Part.Reads.Contains(n.Name)))
        {
            errors.Add(new LoadError(reader.Source, node.Location, $"node '{name}' takes the name of shared resource '{name}', which a part of the same run reads as ${{{name}.<export>}}."));
        }

        foreach (var group in nodes.GroupBy(n => (n.Reader.Layer, n.Name)))
        {
            var first = group.First().Reader;
            foreach (var (reader, name, node) in group.Where(n => n.Reader.Owner.Name != first.Owner.Name))
            {
                errors.Add(new LoadError(reader.Source, node.Location,
                    $"node '{name}' is also declared by '{first.Owner.Name}' ({first.Source}); node names are unique across the parts of one {group.Key.Layer} scope."));
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
                var location = node.Location;
                if (reader.Layer is not null)
                {
                    errors.Add(new LoadError(reader.Source, location, $"a grant node belongs only in a class part or the runtime, not in a part of the {reader.Layer} scope: a grant targets only the workload's own resources."));
                }
                else if (!targets.TryGetValue(node.Template, out var input))
                {
                    errors.Add(new LoadError(reader.Source, $"{location}.template", $"'{node.Template}' is not a grant template in the catalog's {GrantsKind} targets ({string.Join(", ", targets.Keys.Order(StringComparer.Ordinal))}); list it with the input that names its target."));
                }
                else if (!(node.Config.GetValueOrDefault(input) is JValue { Type: JTokenType.String } target
                    && ExpressionEvaluator.SingleReference(Text(target)) is { } reference
                    && (reader.Part.Nodes.ContainsKey(reference.Node) || reference.Node == ExpressionEvaluator.RuntimeNode)))
                {
                    errors.Add(new LoadError(reader.Source, $"{node.ConfigLocation}.{input}", ExpressionEvaluator.GrantTargetRule(input)));
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

    // Only an environment of the last layer deploys a component or runtime part, so an implementation keyed by an earlier layer's type would never
    // be used. Whether a key is a declared type at all is the ref rule's.
    private static void CheckSharedLayerKeys(
        IEnumerable<PartOwner> owners, Dictionary<string, EnvironmentType>? environmentTypes, IReadOnlyList<string> layers, List<LoadError> errors)
    {
        var keyed = owners.SelectMany(o => o.Parts.SelectMany(p => p.Keys).Where(k => k.Role == Roles.EnvironmentType).Distinct().Select(k => (o.Source, Key: k)));
        foreach (var (source, key) in keyed)
        {
            if (environmentTypes?.GetValueOrDefault(key.Key)?.Layer is { } layer && layer != layers[^1])
            {
                errors.Add(new LoadError(source, $"{key.MapLocation}.{key.Key}",
                    $"'{key.Key}' is a {layer}-layer environment type; {key.Map} are keyed by {layers[^1]}-layer types or '{key.Fallback}'."));
            }
        }
    }

    // A workload's class must be valid in every environment, so every implementation declares the same classes: the class map's keys are the
    // inner select keys of the class parts, grouped by the implementation they are in.
    private static void CheckSameClasses(List<PartOwner> components, List<LoadError> errors)
    {
        foreach (var component in components)
        {
            var implementations = component.Parts.Where(p => p.Keys.Count > 1).GroupBy(p => p.Keys[0].Key)
                .Select(i => (Key: i.Key, Location: i.First().Keys[1].MapLocation, Classes: i.Select(p => p.Keys[1].Key).ToList())).ToList();
            var first = implementations[0];
            foreach (var (_, location, classes) in implementations.Where(i => !i.Classes.ToHashSet().SetEquals(first.Classes)))
            {
                errors.Add(new LoadError(component.Source, location,
                    $"declares {string.Join(", ", classes)}; every implementation must declare the classes of '{first.Key}' ({string.Join(", ", first.Classes)})."));
            }
        }
    }

    // Every class part must export exactly the component's contract, so ${resource.<id>.<export>} resolves in every environment.
    private static void CheckExportsContract(List<PartOwner> components, IReadOnlyList<string> layers, List<LoadError> errors)
    {
        foreach (var component in components)
        {
            foreach (var part in component.Parts.Where(p => !layers.Contains(p.Scope)))
            {
                // An export is evaluated in its own requirement's scope, where another requirement's exports are out of reach.
                foreach (var export in part.Exports.Where(e => ExpressionEvaluator.ReadsResource(e.Value)).Select(e => e.Key))
                {
                    errors.Add(new LoadError(component.Source, $"{part.ExportsLocation}.{export}", "an export cannot reference ${resource.<id>.<export>}; it may read its own nodes' outputs, what its component uses, env.* and runtime.*."));
                }

                var missing = component.Contract.Where(e => !part.Exports.ContainsKey(e)).ToList();
                var extra = part.Exports.Keys.Where(e => !component.Contract.Contains(e)).ToList();
                if (missing.Count > 0 || extra.Count > 0)
                {
                    var problems = new[] { missing.Count > 0 ? $"missing {string.Join(", ", missing)}" : null, extra.Count > 0 ? $"not in the contract: {string.Join(", ", extra)}" : null };
                    errors.Add(new LoadError(component.Source, part.ExportsLocation, $"must export exactly the contract of component '{component.Name}' ({string.Join(", ", component.Contract)}); {string.Join("; ", problems.OfType<string>())}."));
                }
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

    // Naming, Roles and Grants files are merged; a key in two files is the define rule's error, and the first is kept.
    private static Dictionary<string, T> Merge<T>(IEnumerable<Document> documents, string property, Func<JToken, T> convert) =>
        documents.SelectMany(document => Map(document.Body[property])).DistinctBy(entry => entry.Key).ToDictionary(entry => entry.Key, entry => convert(entry.Value));

    private static Rollout ToRollout(Document document, PartOwner runtime, Part part)
    {
        // Safe: only schema-valid documents are bound, and runtime.schema.json requires `probe`, `release` and all four probe fields.
        var implementation = document.Body.SelectToken(part.Location)!;
        var probe = implementation["probe"]!;
        var release = implementation["release"]!;
        return new Rollout(
            runtime,
            part,
            new Probe(Text(probe["path"]), (int)probe["expectedStatus"]!, (int)probe["timeoutSeconds"]!, (int)probe["intervalSeconds"]!),
            new RuntimeRelease(
                Text(release["suffixInput"]), Text(release["trafficInput"]), Text(release["trafficOutput"]), Text(release["suffixOutput"]), Text(release["revisionOutput"]),
                Text(release["fqdnOutput"]), Text(release["revisionKey"]), Text(release["latestKey"]), Text(release["weightKey"]), Text(release["resourceIdOutput"])));
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

    private static string Text(JToken? token) => (string?)token ?? string.Empty;

    private static Dictionary<string, JToken> Map(JToken? token) =>
        token is JObject obj ? obj.Properties().ToDictionary(p => p.Name, p => p.Value) : [];
}
