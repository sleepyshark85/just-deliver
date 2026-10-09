using jd.resolver.catalog;
using jd.resolver.expansion;
using jd.resolver.expressions;

namespace jd.resolver.policies;

/// <summary>Which layer supplied a config field's value.</summary>
public enum Layer
{
    Mapping,

    /// <summary>The workload's own definition: its <c>container.variables</c>.</summary>
    Workload,
    PolicySet,
    PolicyDefault,
    PolicyAdd,
}

/// <summary>Where a config field's value came from: the file and its rule (the mapping file, or the policy name and its file; for the workload's variables, the workload file and `container.variables`).</summary>
public sealed record Provenance(string Source, string Rule, Layer Layer)
{
    /// <summary>The one-line form used by the graph JSON and the preview listing; the file is left out when it is the rule itself (mappings).</summary>
    public string Describe() => Source == Rule || Layer == Layer.Workload ? $"{Layer}: {Rule}" : $"{Layer}: {Rule} ({Source})";
}

// Provenance: one entry per leaf config field, keyed by dotted path (consistencyPolicy.level); an array is one leaf.
// Probe is the runtime mapping's probe, on the runtime node only.
public sealed record ResolvedNode(
    string Name,
    string Template,
    NodeKind Kind,
    IReadOnlyDictionary<string, ConfigValue> Config,
    IReadOnlyDictionary<string, Provenance> Provenance,
    Probe? Probe = null);

// The expanded requirement without its pre-policy nodes: Nodes are the ones to use.
public sealed record ResolvedRequirement(
    string Id,
    string Type,
    string Class,
    string Mapping,
    IReadOnlyDictionary<string, EvalResult> Exports,
    IReadOnlyList<ResolvedNode> Nodes);

/// <summary>
/// RuntimeNodes are the nodes of the workload's runtime mapping (none for an environment definition); WorkloadNodes are
/// the nodes added once per workload by workload-scope policies. The result is usable only when Errors is empty.
/// </summary>
public sealed record PolicyResult(
    string CatalogVersion,
    string WorkloadName,
    string WorkloadTeam,
    string? WorkloadImage,
    int? WorkloadPort,
    IReadOnlyList<ResolvedRequirement> Requirements,
    IReadOnlyList<ResolvedNode> RuntimeNodes,
    IReadOnlyList<ResolvedNode> WorkloadNodes,
    IReadOnlyList<LoadError> Errors);
