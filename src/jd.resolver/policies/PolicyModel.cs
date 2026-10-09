using jd.resolver.catalog;
using jd.resolver.expansion;

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

public sealed record ResolvedRequirement(ExpandedRequirement Expanded, IReadOnlyList<ResolvedNode> Nodes);

/// <summary>
/// WorkloadNodes are the nodes added once per workload by workload-scope policies. The result is
/// usable only when Errors is empty.
/// </summary>
public sealed record PolicyResult(
    string CatalogVersion,
    IReadOnlyList<ResolvedRequirement> Requirements,
    IReadOnlyList<ResolvedNode> WorkloadNodes,
    IReadOnlyList<LoadError> Errors);
