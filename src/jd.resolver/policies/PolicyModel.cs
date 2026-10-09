using jd.resolver.catalog;
using jd.resolver.expansion;
using jd.resolver.expressions;

namespace jd.resolver.policies;

/// <summary>Which layer supplied a config field's value.</summary>
public enum Layer
{
    Mapping,
    PolicySet,
    PolicyDefault,
    PolicyAdd,
}

/// <summary>Where a config field's value came from: the catalog file and its rule (the mapping file, or the policy name).</summary>
public sealed record Provenance(string Source, string Rule, Layer Layer);

// Provenance: one entry per leaf config field, keyed by dotted path (consistencyPolicy.level); an array is one leaf.
public sealed record ResolvedNode(
    string Name,
    string Template,
    NodeKind Kind,
    IReadOnlyDictionary<string, ConfigValue> Config,
    IReadOnlyDictionary<string, Provenance> Provenance);

// The expanded requirement without its pre-policy nodes: Nodes are the ones to use.
public sealed record ResolvedRequirement(
    string Id,
    string Type,
    string Class,
    string Mapping,
    IReadOnlyDictionary<string, EvalResult> Exports,
    IReadOnlyList<ResolvedNode> Nodes);

/// <summary>
/// WorkloadNodes are the nodes added once per workload by workload-scope policies. The result is
/// usable only when Errors is empty.
/// </summary>
public sealed record PolicyResult(
    string CatalogVersion,
    string WorkloadName,
    IReadOnlyList<ResolvedRequirement> Requirements,
    IReadOnlyList<ResolvedNode> WorkloadNodes,
    IReadOnlyList<LoadError> Errors);
