using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.policies;

/// <summary>
/// Applies the catalog's policies to expanded requirements and records where every config field came from. Pure; the
/// catalog is never modified. Workload-scope policies apply to a workload only (an environment definition has no runtime): they set and
/// default fields of the runtime node and require components, never add nodes.
/// Rules: docs/architecture/resolver.md (Engine rules, Policies).
/// </summary>
public sealed class PolicyApplier(Catalog catalog, EnvironmentDescriptor environment)
{
    // Policy name order makes conflict reports and "same value" provenance independent of file order.
    private readonly IReadOnlyList<Policy> _policies = catalog.Policies.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

    /// <summary>The expansion with every matching policy applied and each config field's provenance; its errors are the expansion's and the policies' (conflicts, required components).</summary>
    public PolicyResult Apply(ExpansionResult expansion)
    {
        // A requirement the expansion dropped is not in the result, so its errors must carry through.
        var errors = new List<LoadError>(expansion.Errors);
        // An entries field is the workload's variables (the expander gives them to the runtime node only), written in its file.
        var variables = new Provenance(expansion.OwnerFile, expansion.VariablesLocation, Layer.Workload);
        ExpressionEvaluator EvaluatorFor(string currentId, IEnumerable<string> nodeNames) => new(new ExpressionContext(
            catalog.Roles, catalog.Naming, environment, expansion.Owner, new ExpressionScope(currentId, nodeNames.ToHashSet(), new Dictionary<Reference, string>(), expansion.Parts)));

        var requirements = new List<ResolvedRequirement>();
        foreach (var requirement in expansion.Requirements)
        {
            // An environment run's parts share one scope, so a policy value may read any node of it.
            var evaluator = EvaluatorFor(requirement.Id, expansion.Requirements.Where(r => r.Id == requirement.Id).SelectMany(r => r.Nodes).Select(n => n.Name));
            var nodeOwner = new NodeOwner(new Provenance(requirement.Source, requirement.Source, Layer.Implementation), requirement.Type, requirement.Class, []);
            var nodes = requirement.Nodes.Select(n => ApplyNodePolicies(n, nodeOwner, variables, new NodeRun($"{requirement.Id}/{n.Name}", evaluator, errors))).ToList();
            requirements.Add(new ResolvedRequirement(requirement.Id, requirement.Type, requirement.Class, requirement.Source, requirement.Exports, nodes)
            {
                ExportLocations = requirement.ExportLocations,
            });
        }

        var workloadPolicies = _policies.Where(p => expansion.Kind == OwnerKind.Workload && IsWorkloadScope(p)).ToList();

        // A declared requirement that failed to expand still counts; its own error says why.
        foreach (var policy in workloadPolicies.Where(p => p.Require.Value is { } type && !expansion.DeclaredTypes.Contains(type)))
        {
            errors.Add(new LoadError(expansion.OwnerFile, expansion.RequirementsLocation,
                $"policy '{policy.Name}' ({policy.Source}) requires a '{policy.Require.Value}' requirement ({policy.Reason}); add '- type: {policy.Require.Value}'."));
        }

        // The runtime's nodes get node-scope policies like any component node; the runtime node also gets the workload-scope
        // policies' set and default (their paths start with 'runtime.', which the catalog loader enforces).
        var runtimeNodes = new List<ResolvedNode>();
        if (expansion.Runtime is { } runtime)
        {
            var runtimeNames = runtime.Nodes.Select(n => n.Name).ToList();
            var origin = new Provenance(runtime.Source, runtime.Source, Layer.Implementation);
            foreach (var n in runtime.Nodes)
            {
                var nodeOwner = new NodeOwner(origin, null, null, n.Name == ExpressionEvaluator.RuntimeNode ? workloadPolicies : []);
                var node = ApplyNodePolicies(n, nodeOwner, variables, new NodeRun(n.Name, EvaluatorFor(n.Name, runtimeNames), errors));
                runtimeNodes.Add(n.Name == ExpressionEvaluator.RuntimeNode ? node with { Probe = runtime.Probe, Release = runtime.Release } : node);
            }
        }

        var owner = expansion.Owner;
        return new PolicyResult(catalog.Version, owner.Name, owner.Team, owner.Image, owner.Port, requirements, runtimeNodes, errors, expansion.Parts);
    }

    // A policy that names a scope is a workload-scope policy (the catalog loader refuses any other scope). It applies where its criteria hold
    // without a node: only the environment's values are there. Its runtime is not matched yet: a workload names no runtime.
    private bool IsWorkloadScope(Policy policy) => policy.Scope.Value is not null && Holds(policy, ValuesAt(null, null, null));

    // The value each role a criterion compares with has: the environment's type, and at a node its template and its requirement's type and
    // class. A node not tied to a requirement has no type or class, and workload scope has no node.
    private Dictionary<string, string?> ValuesAt(string? template, string? type, string? cls) => new()
    {
        [Roles.EnvironmentType] = environment.Type,
        [Roles.NodeTemplate] = template,
        [Roles.RequirementType] = type,
        [Roles.RequirementClass] = cls,
    };

    // Every criterion the policy gives a value holds: the value equals the role's, so a role without a value holds none. A criterion on a role
    // ValuesAt does not supply is a jd bug (the schema declares a match this applier does not know), so it throws rather than never applying.
    private static bool Holds(Policy policy, Dictionary<string, string?> values) =>
        policy.Criteria.All(criterion => criterion.Value is null || criterion.Value == values[criterion.Role]);

    // The node's config with the matching policies applied, and where each field came from; the variables' provenance is that of its entries fields.
    private ResolvedNode ApplyNodePolicies(ExpandedNode node, NodeOwner owner, Provenance variables, NodeRun run)
    {
        var config = new ConfigObject(node.Config);
        var provenance = new Dictionary<string, Provenance>();
        Record(provenance, string.Empty, config, owner.Origin);
        foreach (var (field, entries) in config.Properties.Where(p => p.Value is ConfigObject { AsEntries: true }))
        {
            Record(provenance, field, entries, variables);
        }

        // A node-scope policy applies where each of its criteria holds at the node.
        var values = ValuesAt(node.Template, owner.Type, owner.Class);
        var matching = _policies.Where(p => p.Scope.Value is null && Holds(p, values)).Concat(owner.WorkloadPolicies).ToList();

        // A workload-scope policy addresses the runtime node by name: its paths here have that name removed, but are reported as written.
        List<NodeEntry> Entries(Func<Policy, PolicyEntries> of) =>
            [.. matching.SelectMany(p => owner.WorkloadPolicies.Contains(p)
                ? of(p).Entries.Where(e => e.Path.StartsWith(node.Name + ConfigLeaves.PathSeparator, StringComparison.Ordinal)).Select(e => new NodeEntry(p, of(p).Name, e.Path[(node.Name.Length + 1)..], e))
                : of(p).Entries.Select(e => new NodeEntry(p, of(p).Name, e.Path, e)))];

        // Sets replace whatever is there; defaults only fill what is absent after the sets.
        config = ApplyLayer(config, provenance, Entries(p => p.Set), Layer.PolicySet, run);
        config = ApplyLayer(config, provenance, Entries(p => p.Default), Layer.PolicyDefault, run);
        return new ResolvedNode(node.Name, node.Template, node.Kind, config.Properties, provenance);
    }

    private static ConfigObject ApplyLayer(ConfigObject config, Dictionary<string, Provenance> provenance, List<NodeEntry> all, Layer layer, NodeRun run)
    {
        var (label, evaluator, errors) = run;
        var overlapping = ReportOverlaps(all, run);

        var byPath = all.Where(e => !overlapping.Contains(e.Path)).GroupBy(e => e.Path).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in byPath)
        {
            var first = group.First();
            var (policy, field, entry) = (first.Policy, first.Field, first.Entry);
            if (group.FirstOrDefault(e => !JToken.DeepEquals(e.Entry.Value, entry.Value)) is { Policy: { } other })
            {
                errors.Add(new LoadError(policy.Source, entry.Location,
                    $"policies '{policy.Name}' ({policy.Source}) and '{other.Name}' ({other.Source}) give field '{entry.Path}' of node '{label}' different {field} values."));
                continue;
            }

            var path = first.Path.Split(ConfigLeaves.PathSeparator);
            if (EntriesMisuse(config, path, entry.Value) is { } misuse)
            {
                errors.Add(new LoadError(policy.Source, entry.Location, $"policy '{policy.Name}' gives '{entry.Path}' of node '{label}': {misuse}"));
                continue;
            }

            if (layer == Layer.PolicyDefault && Get(config, path) is not null)
            {
                continue;
            }

            if (ConfigWalker.Walk(entry.Value, evaluator, policy.Source, entry.Location, errors) is not { } value)
            {
                continue;
            }

            if (Put(config, path, 0, value) is not { } updated)
            {
                errors.Add(new LoadError(policy.Source, entry.Location, $"cannot create field '{entry.Path}' of node '{label}': a parent on the path is not an object."));
                continue;
            }

            config = updated;
            foreach (var key in provenance.Keys.Where(k => k == first.Path || k.StartsWith(first.Path + ConfigLeaves.PathSeparator, StringComparison.Ordinal)).ToList())
            {
                provenance.Remove(key);
            }

            Record(provenance, first.Path, value, new Provenance(policy.Source, policy.Name, layer));
        }

        return config;
    }

    // A path that is a strict prefix of another policy's path would make one policy's value depend on application order: every such pair is
    // reported, and the paths they involve are returned, for the caller to leave out.
    private static HashSet<string> ReportOverlaps(List<NodeEntry> all, NodeRun run)
    {
        var overlapping = new HashSet<string>();
        foreach (var outer in all)
        {
            foreach (var inner in all.Where(i => i.Policy.Name != outer.Policy.Name && i.Path.StartsWith(outer.Path + ConfigLeaves.PathSeparator, StringComparison.Ordinal)))
            {
                run.Errors.Add(new LoadError(outer.Policy.Source, outer.Entry.Location,
                    $"policies '{outer.Policy.Name}' ({outer.Policy.Source}) and '{inner.Policy.Name}' ({inner.Policy.Source}) give overlapping {outer.Field} fields '{outer.Entry.Path}' and '{inner.Entry.Path}' of node '{run.Label}'."));
                overlapping.UnionWith([outer.Path, inner.Path]);
            }
        }

        return overlapping;
    }

    // An entries field (the workload's variables) takes one key per name, each a string (an expression string is fine): not the field as a whole,
    // which would drop its list form, not a path below a key, and not another kind of value.
    private static string? EntriesMisuse(ConfigObject config, string[] path, JToken value)
    {
        for (var length = 1; length <= path.Length; length++)
        {
            if (Get(config, path[..length]) is ConfigObject { AsEntries: true })
            {
                return length == path.Length
                    ? "it is the list built from a map of entries; give its keys instead."
                    : length < path.Length - 1 ? "an entry is a single value; give a key of the entries, not a path below it."
                    : value is JValue { Type: JTokenType.String } ? null : "an entry is a string.";
            }
        }

        return null;
    }

    private static ConfigValue? Get(ConfigObject config, string[] path)
    {
        ConfigValue current = config;
        foreach (var segment in path)
        {
            if (current is not ConfigObject obj || !obj.Properties.TryGetValue(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    // Copy-on-write: returns the updated object, creating missing intermediate objects; null when a parent is not an object.
    private static ConfigObject? Put(ConfigObject obj, string[] path, int index, ConfigValue value)
    {
        var properties = new Dictionary<string, ConfigValue>(obj.Properties);
        if (index == path.Length - 1)
        {
            properties[path[index]] = value;
            return obj with { Properties = properties };
        }

        var child = properties.GetValueOrDefault(path[index]) ?? new ConfigObject(new Dictionary<string, ConfigValue>());
        if (child is not ConfigObject childObject || Put(childObject, path, index + 1, value) is not { } updated)
        {
            return null;
        }

        properties[path[index]] = updated;
        return obj with { Properties = properties };
    }

    // The requirement or runtime a node comes from: the provenance of its config as written, the type and class node-scope policies match,
    // and the workload-scope policies that apply to it (the runtime node's only).
    private sealed record NodeOwner(Provenance Origin, string? Type, string? Class, IReadOnlyList<Policy> WorkloadPolicies);

    // One node's policy run: how its errors name it, the evaluator of its scope, and where its errors go.
    private sealed record NodeRun(string Label, ExpressionEvaluator Evaluator, List<LoadError> Errors);

    // A policy's set or default entry for one node: the field holding it, as messages name it, the path in the node's config, and the entry as
    // the policy wrote it.
    private sealed record NodeEntry(Policy Policy, string Field, string Path, PolicyEntry Entry);

    // Records the provenance of every leaf under value, keyed by dotted path.
    private static void Record(Dictionary<string, Provenance> provenance, string path, ConfigValue value, Provenance origin)
    {
        foreach (var (leafPath, _) in ConfigLeaves.Of(path, value))
        {
            provenance[leafPath] = origin;
        }
    }
}
