using System.Collections.ObjectModel;
using jd.resolver.catalog;
using jd.resolver.expansion;
using jd.resolver.expressions;

namespace jd.resolver.policies;

/// <summary>Which layer supplied a config field's value.</summary>
public enum Layer
{
    /// <summary>The catalog's component or runtime implementation the node comes from.</summary>
    Implementation,

    /// <summary>The workload's own definition: its variables.</summary>
    Workload,

    /// <summary>A policy's <c>set</c>: the value replaces whatever the earlier layers gave.</summary>
    PolicySet,

    /// <summary>A policy's <c>default</c>: the value applies only where no earlier layer gave one.</summary>
    PolicyDefault,
}

/// <summary>Where a config field's value came from: the file and its rule (the component or runtime file, or the policy name and its file; for the workload's variables, the workload file and where it holds them).</summary>
public sealed record Provenance(string Source, string Rule, Layer Layer)
{
    /// <summary>The one-line form used by the graph JSON and the preview listing; the file is left out when it is the rule itself (implementations).</summary>
    public string Describe() => Source == Rule || Layer == Layer.Workload ? $"{Layer}: {Rule}" : $"{Layer}: {Rule} ({Source})";
}

/// <summary>
/// A node after the policies: its final config and where each field came from. <see cref="Provenance"/>: one entry per leaf config field,
/// keyed by dotted path (consistencyPolicy.level); an array is one leaf. <see cref="Probe"/> and <see cref="Release"/> are the runtime's, on
/// the runtime node only.
/// </summary>
public sealed record ResolvedNode(
    string Name,
    string Template,
    NodeKind Kind,
    IReadOnlyDictionary<string, ConfigValue> Config,
    IReadOnlyDictionary<string, Provenance> Provenance,
    Probe? Probe = null,
    RuntimeRelease? Release = null);

/// <summary>The expanded requirement without its pre-policy nodes: <see cref="Nodes"/> are the ones to use.</summary>
public sealed record ResolvedRequirement(
    string Id,
    string? Type,
    string? Class,
    string Source,
    IReadOnlyDictionary<string, EvalResult> Exports,
    IReadOnlyList<ResolvedNode> Nodes)
{
    /// <summary>Where <see cref="Source"/> holds each export, by name (<see cref="ExpandedRequirement.ExportLocations"/>).</summary>
    internal IReadOnlyDictionary<string, string> ExportLocations { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}

/// <summary>
/// RuntimeNodes are the nodes of the workload's runtime (none for an environment definition); Parts are an environment run's parts'
/// exports, as expanded. The result is usable only when
/// Errors is empty.
/// </summary>
public sealed record PolicyResult(
    string CatalogVersion,
    string WorkloadName,
    string WorkloadTeam,
    string? WorkloadImage,
    int? WorkloadPort,
    IReadOnlyList<ResolvedRequirement> Requirements,
    IReadOnlyList<ResolvedNode> RuntimeNodes,
    IReadOnlyList<LoadError> Errors,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parts);
