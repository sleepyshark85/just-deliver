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
    private const char PathSeparator = '.';

    // The source shown for the runtime's variables that come from the workload's definition (the file is the workload being resolved).
    private const string WorkloadVariablesRule = "container.variables";

    // Policy name order makes conflict reports and "same value" provenance independent of file order.
    private readonly IReadOnlyList<Policy> _policies = catalog.Policies.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

    public PolicyResult Apply(ExpansionResult expansion)
    {
        // A requirement the expansion dropped is not in the result, so its errors must carry through.
        var errors = new List<LoadError>(expansion.Errors);
        ExpressionEvaluator EvaluatorFor(string currentId, IEnumerable<string> nodeNames) => new(new ExpressionContext(
            catalog.Roles, catalog.Naming, environment, expansion.WorkloadName, expansion.WorkloadTeam, currentId, nodeNames.ToHashSet(), new Dictionary<Reference, string>(),
            expansion.WorkloadImage, expansion.WorkloadPort, expansion.Parts));

        var requirements = new List<ResolvedRequirement>();
        foreach (var requirement in expansion.Requirements)
        {
            // An environment run's parts share one scope, so a policy value may read any node of it.
            var evaluator = EvaluatorFor(requirement.Id, expansion.Requirements.Where(r => r.Id == requirement.Id).SelectMany(r => r.Nodes).Select(n => n.Name));
            var nodes = requirement.Nodes
                .Select(n => ApplyNodePolicies(
                    n.Name, n.Template, n.Kind, new ConfigObject(n.Config), new Provenance(requirement.Source, requirement.Source, Layer.Implementation),
                    requirement.Type, requirement.Class, evaluator, $"{requirement.Id}/{n.Name}", [], string.Empty, errors))
                .ToList();
            requirements.Add(new ResolvedRequirement(requirement.Id, requirement.Type, requirement.Class, requirement.Source, requirement.Exports, nodes));
        }

        var workloadPolicies = _policies.Where(p => expansion.Owner == OwnerKind.Workload && IsWorkloadScope(p)).ToList();

        // A declared requirement that failed to expand still counts; its own error says why.
        foreach (var policy in workloadPolicies.Where(p => p.Require is { } type && !expansion.DeclaredTypes.Contains(type)))
        {
            errors.Add(new LoadError(expansion.OwnerFile, "requires",
                $"policy '{policy.Name}' ({policy.Source}) requires a '{policy.Require}' requirement ({policy.Reason}); add '- type: {policy.Require}'."));
        }

        // The runtime's nodes get node-scope policies like any component node; the runtime node also gets the workload-scope
        // policies' set and default (their paths start with 'runtime.', which the catalog loader enforces).
        var runtimeNodes = new List<ResolvedNode>();
        if (expansion.Runtime is { } runtime)
        {
            var runtimeNames = runtime.Nodes.Select(n => n.Name).ToList();
            foreach (var n in runtime.Nodes)
            {
                var node = ApplyNodePolicies(
                    n.Name, n.Template, n.Kind, new ConfigObject(n.Config), new Provenance(runtime.Source, runtime.Source, Layer.Implementation),
                    null, null, EvaluatorFor(n.Name, runtimeNames), n.Name, n.Name == ExpressionEvaluator.RuntimeNode ? workloadPolicies : [], expansion.OwnerFile, errors);
                runtimeNodes.Add(n.Name == ExpressionEvaluator.RuntimeNode ? node with { Probe = runtime.Probe, Release = runtime.Release } : node);
            }
        }

        return new PolicyResult(catalog.Version, expansion.WorkloadName, expansion.WorkloadTeam, expansion.WorkloadImage, expansion.WorkloadPort, requirements, runtimeNodes, errors, expansion.Parts);
    }

    // Workload scope is kind: runtime, optionally narrowed by environmentType. The 'runtime' key is accepted but not matched yet:
    // a workload names no runtime.
    private bool IsWorkloadScope(Policy policy)
    {
        var criteria = policy.Match.Criteria;
        return criteria.GetValueOrDefault(MatchCriteria.KindKey) == MatchCriteria.RuntimeKind
            && criteria.All(c => c.Key switch
            {
                MatchCriteria.KindKey or MatchCriteria.RuntimeKey => true,
                MatchCriteria.EnvironmentTypeKey => c.Value == environment.Type,
                _ => false,
            });
    }

    // Node scope: only type/class/environmentType/template keys, each equal to the node's value. A node not tied to a requirement
    // has no type or class, so a policy matching on them does not apply to it.
    private bool MatchesNode(Policy policy, string template, string? type, string? cls) =>
        policy.Match.Criteria.All(c => c.Key switch
        {
            MatchCriteria.TypeKey => c.Value == type,
            MatchCriteria.ClassKey => c.Value == cls,
            MatchCriteria.EnvironmentTypeKey => c.Value == environment.Type,
            MatchCriteria.TemplateKey => c.Value == template,
            _ => false,
        });

    private ResolvedNode ApplyNodePolicies(
        string name, string template, NodeKind kind, ConfigObject config, Provenance origin,
        string? type, string? cls, ExpressionEvaluator evaluator, string label, List<Policy> workloadPolicies, string workloadFile, List<LoadError> errors)
    {
        var provenance = new Dictionary<string, Provenance>();
        Record(provenance, string.Empty, config, origin);
        foreach (var (field, entries) in config.Properties.Where(p => p.Value is ConfigObject { AsEntries: true }))
        {
            Record(provenance, field, entries, new Provenance(workloadFile, WorkloadVariablesRule, Layer.Workload));
        }

        var matching = _policies.Where(p => MatchesNode(p, template, type, cls)).Concat(workloadPolicies).ToList();

        // A workload-scope policy addresses the runtime node by name: its paths here have that name removed, but are reported as written.
        Func<Policy, IEnumerable<(string Path, string Shown, JToken Value)>> Scoped(Func<Policy, IReadOnlyDictionary<string, JToken>> entries) =>
            p => workloadPolicies.Contains(p)
                ? entries(p).Where(e => e.Key.StartsWith(name + PathSeparator, StringComparison.Ordinal)).Select(e => (e.Key[(name.Length + 1)..], e.Key, e.Value))
                : entries(p).Select(e => (e.Key, e.Key, e.Value));

        // Sets replace whatever is there; defaults only fill what is absent after the sets.
        config = ApplyLayer(config, provenance, matching, Scoped(p => p.Set), Layer.PolicySet, evaluator, label, errors);
        config = ApplyLayer(config, provenance, matching, Scoped(p => p.Default), Layer.PolicyDefault, evaluator, label, errors);
        return new ResolvedNode(name, template, kind, config.Properties, provenance);
    }

    private static ConfigObject ApplyLayer(
        ConfigObject config, Dictionary<string, Provenance> provenance, List<Policy> matching,
        Func<Policy, IEnumerable<(string Path, string Shown, JToken Value)>> entries, Layer layer,
        ExpressionEvaluator evaluator, string label, List<LoadError> errors)
    {
        var isDefault = layer == Layer.PolicyDefault;
        var field = isDefault ? "default" : "set";
        var all = matching.SelectMany(p => entries(p).Select(e => (Policy: p, e.Path, e.Shown, e.Value))).ToList();

        // A path that is a strict prefix of another policy's path would make one policy's value depend on application order.
        var overlapping = new HashSet<string>();
        foreach (var outer in all)
        {
            foreach (var inner in all.Where(i => i.Policy.Name != outer.Policy.Name && i.Path.StartsWith(outer.Path + PathSeparator, StringComparison.Ordinal)))
            {
                errors.Add(new LoadError(outer.Policy.Source, $"{field}.{outer.Shown}",
                    $"policies '{outer.Policy.Name}' ({outer.Policy.Source}) and '{inner.Policy.Name}' ({inner.Policy.Source}) give overlapping {field} fields '{outer.Shown}' and '{inner.Shown}' of node '{label}'."));
                overlapping.UnionWith([outer.Path, inner.Path]);
            }
        }

        var byPath = all.Where(e => !overlapping.Contains(e.Path)).GroupBy(e => e.Path).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in byPath)
        {
            var first = group.First();
            if (group.FirstOrDefault(e => !JToken.DeepEquals(e.Value, first.Value)) is { Policy: { } other })
            {
                errors.Add(new LoadError(first.Policy.Source, $"{field}.{first.Shown}",
                    $"policies '{first.Policy.Name}' ({first.Policy.Source}) and '{other.Name}' ({other.Source}) give field '{first.Shown}' of node '{label}' different {field} values."));
                continue;
            }

            var path = first.Path.Split(PathSeparator);
            if (EntriesMisuse(config, path, first.Value) is { } misuse)
            {
                errors.Add(new LoadError(first.Policy.Source, $"{field}.{first.Shown}", $"policy '{first.Policy.Name}' gives '{first.Shown}' of node '{label}': {misuse}"));
                continue;
            }

            if (isDefault && Get(config, path) is not null)
            {
                continue;
            }

            if (ConfigWalker.Walk(first.Value, evaluator, first.Policy.Source, $"{field}.{first.Shown}", errors) is not { } value)
            {
                continue;
            }

            if (Put(config, path, 0, value) is not { } updated)
            {
                errors.Add(new LoadError(first.Policy.Source, $"{field}.{first.Shown}", $"cannot create field '{first.Shown}' of node '{label}': a parent on the path is not an object."));
                continue;
            }

            config = updated;
            foreach (var key in provenance.Keys.Where(k => k == first.Path || k.StartsWith(first.Path + PathSeparator, StringComparison.Ordinal)).ToList())
            {
                provenance.Remove(key);
            }

            Record(provenance, first.Path, value, new Provenance(first.Policy.Source, first.Policy.Name, layer));
        }

        return config;
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

    // Records the provenance of every leaf under value, keyed by dotted path.
    private static void Record(Dictionary<string, Provenance> provenance, string path, ConfigValue value, Provenance origin)
    {
        foreach (var (leafPath, _) in ConfigLeaves.Of(path, value))
        {
            provenance[leafPath] = origin;
        }
    }
}
