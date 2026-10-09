using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.policies;

/// <summary>
/// Applies the catalog's policies to expanded requirements and records where every config field came from. Pure; the
/// catalog is never modified. One pass: nodes added by policies receive node-scope policies but never trigger more adds.
/// Rules: docs/architecture/resolver.md (Engine rules, Policies).
/// </summary>
public sealed class PolicyApplier(Catalog catalog, EnvironmentDescriptor environment)
{
    private const char PathSeparator = '.';

    // Policy name order makes conflict reports and "same value" provenance independent of file order.
    private readonly IReadOnlyList<Policy> _policies = catalog.Policies.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

    public PolicyResult Apply(ExpansionResult expansion)
    {
        // A requirement the expansion dropped is not in the result, so its errors must carry through.
        var errors = new List<LoadError>(expansion.Errors);
        ExpressionEvaluator EvaluatorFor(string currentId, IEnumerable<string> nodeNames) => new(new ExpressionContext(
            catalog.Roles, catalog.Naming, environment, expansion.WorkloadName, expansion.WorkloadTeam, currentId, nodeNames.ToHashSet(), new Dictionary<Reference, string>()));

        var requirements = new List<ResolvedRequirement>();
        foreach (var requirement in expansion.Requirements)
        {
            var evaluator = EvaluatorFor(requirement.Id, requirement.Nodes.Select(n => n.Name));
            var nodes = requirement.Nodes
                .Select(n => ApplyNodePolicies(
                    n.Name, n.Template, n.Kind, new ConfigObject(n.Config), new Provenance(requirement.Mapping, requirement.Mapping, Layer.Mapping),
                    requirement.Type, requirement.Class, evaluator, $"{requirement.Id}/{n.Name}", errors))
                .ToList();
            requirements.Add(new ResolvedRequirement(requirement.Id, requirement.Type, requirement.Class, requirement.Mapping, requirement.Exports, nodes));
        }

        var workloadNodes = new List<ResolvedNode>();
        var addedBy = new Dictionary<string, Policy>();
        foreach (var policy in _policies.Where(IsWorkloadScope))
        {
            foreach (var (name, node) in policy.Add)
            {
                if (!addedBy.TryAdd(name, policy))
                {
                    errors.Add(new LoadError(policy.Source, $"add.{name}", $"policies '{addedBy[name].Name}' ({addedBy[name].Source}) and '{policy.Name}' ({policy.Source}) both add a node named '{name}'."));
                    continue;
                }

                var evaluator = EvaluatorFor(name, policy.Add.Keys);
                var config = ConfigWalker.WalkConfig(node.Config, evaluator, policy.Source, $"add.{name}.config", errors);
                workloadNodes.Add(ApplyNodePolicies(
                    name, node.Template, node.Kind, config, new Provenance(policy.Source, policy.Name, Layer.PolicyAdd), null, null, evaluator, name, errors));
            }
        }

        return new PolicyResult(catalog.Version, expansion.WorkloadName, expansion.WorkloadTeam, requirements, workloadNodes, errors);
    }

    // Workload scope is kind: runtime, optionally narrowed by tier. The 'runtime' key is accepted but not matched yet:
    // the definition names no runtime until the runtime-mapping slice.
    private bool IsWorkloadScope(Policy policy)
    {
        var criteria = policy.Match.Criteria;
        return criteria.GetValueOrDefault(MatchCriteria.KindKey) == MatchCriteria.RuntimeKind
            && criteria.All(c => c.Key switch
            {
                MatchCriteria.KindKey or MatchCriteria.RuntimeKey => true,
                MatchCriteria.TierKey => c.Value == environment.Tier,
                _ => false,
            });
    }

    // Node scope: only type/class/tier/template keys, each equal to the node's value. A node not tied to a requirement
    // has no type or class, so a policy matching on them does not apply to it.
    private bool MatchesNode(Policy policy, string template, string? type, string? cls) =>
        policy.Match.Criteria.All(c => c.Key switch
        {
            MatchCriteria.TypeKey => c.Value == type,
            MatchCriteria.ClassKey => c.Value == cls,
            MatchCriteria.TierKey => c.Value == environment.Tier,
            MatchCriteria.TemplateKey => c.Value == template,
            _ => false,
        });

    private ResolvedNode ApplyNodePolicies(
        string name, string template, NodeKind kind, ConfigObject config, Provenance origin,
        string? type, string? cls, ExpressionEvaluator evaluator, string label, List<LoadError> errors)
    {
        var provenance = new Dictionary<string, Provenance>();
        Record(provenance, string.Empty, config, origin);
        var matching = _policies.Where(p => MatchesNode(p, template, type, cls)).ToList();

        // Sets replace whatever is there; defaults only fill what is absent after the sets.
        config = ApplyLayer(config, provenance, matching, p => p.Set, Layer.PolicySet, evaluator, label, errors);
        config = ApplyLayer(config, provenance, matching, p => p.Default, Layer.PolicyDefault, evaluator, label, errors);
        return new ResolvedNode(name, template, kind, config.Properties, provenance);
    }

    private static ConfigObject ApplyLayer(
        ConfigObject config, Dictionary<string, Provenance> provenance, List<Policy> matching,
        Func<Policy, IReadOnlyDictionary<string, JToken>> entries, Layer layer,
        ExpressionEvaluator evaluator, string label, List<LoadError> errors)
    {
        var isDefault = layer == Layer.PolicyDefault;
        var field = isDefault ? "default" : "set";
        var all = matching.SelectMany(p => entries(p).Select(e => (Policy: p, Path: e.Key, Value: e.Value))).ToList();

        // A path that is a strict prefix of another policy's path would make one policy's value depend on application order.
        var overlapping = new HashSet<string>();
        foreach (var outer in all)
        {
            foreach (var inner in all.Where(i => i.Policy.Name != outer.Policy.Name && i.Path.StartsWith(outer.Path + PathSeparator, StringComparison.Ordinal)))
            {
                errors.Add(new LoadError(outer.Policy.Source, $"{field}.{outer.Path}",
                    $"policies '{outer.Policy.Name}' ({outer.Policy.Source}) and '{inner.Policy.Name}' ({inner.Policy.Source}) give overlapping {field} fields '{outer.Path}' and '{inner.Path}' of node '{label}'."));
                overlapping.UnionWith([outer.Path, inner.Path]);
            }
        }

        var byPath = all.Where(e => !overlapping.Contains(e.Path)).GroupBy(e => e.Path).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in byPath)
        {
            var first = group.First();
            if (group.FirstOrDefault(e => !JToken.DeepEquals(e.Value, first.Value)) is { Policy: { } other })
            {
                errors.Add(new LoadError(first.Policy.Source, $"{field}.{first.Path}",
                    $"policies '{first.Policy.Name}' ({first.Policy.Source}) and '{other.Name}' ({other.Source}) give field '{first.Path}' of node '{label}' different {field} values."));
                continue;
            }

            var path = first.Path.Split(PathSeparator);
            if (isDefault && IsPresent(config, path))
            {
                continue;
            }

            if (ConfigWalker.Walk(first.Value, evaluator, first.Policy.Source, $"{field}.{first.Path}", errors) is not { } value)
            {
                continue;
            }

            if (Put(config, path, 0, value) is not { } updated)
            {
                errors.Add(new LoadError(first.Policy.Source, $"{field}.{first.Path}", $"cannot create field '{first.Path}' of node '{label}': a parent on the path is not an object."));
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

    private static bool IsPresent(ConfigObject config, string[] path)
    {
        ConfigValue current = config;
        foreach (var segment in path)
        {
            if (current is not ConfigObject obj || !obj.Properties.TryGetValue(segment, out var next))
            {
                return false;
            }

            current = next;
        }

        return true;
    }

    // Copy-on-write: returns the updated object, creating missing intermediate objects; null when a parent is not an object.
    private static ConfigObject? Put(ConfigObject obj, string[] path, int index, ConfigValue value)
    {
        var properties = new Dictionary<string, ConfigValue>(obj.Properties);
        if (index == path.Length - 1)
        {
            properties[path[index]] = value;
            return new ConfigObject(properties);
        }

        var child = properties.GetValueOrDefault(path[index]) ?? new ConfigObject(new Dictionary<string, ConfigValue>());
        if (child is not ConfigObject childObject || Put(childObject, path, index + 1, value) is not { } updated)
        {
            return null;
        }

        properties[path[index]] = updated;
        return new ConfigObject(properties);
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
