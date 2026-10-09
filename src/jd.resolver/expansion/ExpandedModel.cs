using jd.resolver.catalog;
using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.expansion;

/// <summary>A config value after evaluation. The tree mirrors the mapping's config: strings are evaluated, everything else is carried over.</summary>
public abstract record ConfigValue;

/// <summary>A non-string scalar, copied from the catalog.</summary>
public sealed record ConfigScalar(JToken Value) : ConfigValue;

/// <summary>A string, evaluated: <see cref="Resolved"/> or <see cref="Pending"/> on references. EnvPaths are the <c>env.</c> paths it read.</summary>
public sealed record ConfigText(EvalResult Result, IReadOnlySet<string> EnvPaths) : ConfigValue;

/// <summary>
/// A mapping of names to values. <paramref name="AsEntries"/> marks one declared with <c>fn::entries</c>: it stays a map
/// while policies address its keys, and is deployed as a list of <c>{name, value}</c> items sorted by name.
/// </summary>
public sealed record ConfigObject(IReadOnlyDictionary<string, ConfigValue> Properties, bool AsEntries = false) : ConfigValue;

public sealed record ConfigArray(IReadOnlyList<ConfigValue> Items) : ConfigValue;

public sealed record ExpandedNode(string Name, string Template, NodeKind Kind, IReadOnlyDictionary<string, ConfigValue> Config);

/// <summary>One requirement expanded from the mapping in <see cref="Mapping"/> (catalog-relative path of the selected file).</summary>
public sealed record ExpandedRequirement(
    string Id,
    string Type,
    string Class,
    string Mapping,
    IReadOnlyList<ExpandedNode> Nodes,
    IReadOnlyDictionary<string, EvalResult> Exports);

/// <summary>The workload's runtime nodes expanded from the runtime mapping in <see cref="Mapping"/>, with the mapping's probe.</summary>
public sealed record ExpandedRuntime(string Mapping, IReadOnlyList<ExpandedNode> Nodes, Probe? Probe);

/// <summary>Who the requirements belong to: a workload (it has a runtime), or an environment definition (substrate, no runtime).</summary>
public enum OwnerKind
{
    Workload,
    Environment,
}

/// <param name="Owner">Whether the requirements are a workload's or an environment definition's.</param>
/// <param name="WorkloadName">The workload's <c>metadata.name</c>, or <c>@</c> and the environment's name; carried on for policy evaluation.</param>
/// <param name="WorkloadTeam">The workload's <c>metadata.team</c>.</param>
/// <param name="WorkloadImage">The workload's <c>container.image</c>; null for an environment definition.</param>
/// <param name="WorkloadPort">The first port of the workload's <c>container.ports</c>, if any.</param>
/// <param name="Requirements">Requirements that expanded cleanly; a requirement with any error is left out.</param>
/// <param name="Runtime">The runtime nodes of a workload; null for an environment definition, and when the runtime failed to expand.</param>
/// <param name="Errors">Everything that kept a requirement from expanding.</param>
public sealed record ExpansionResult(
    OwnerKind Owner,
    string WorkloadName,
    string WorkloadTeam,
    string? WorkloadImage,
    int? WorkloadPort,
    IReadOnlyList<ExpandedRequirement> Requirements,
    ExpandedRuntime? Runtime,
    IReadOnlyList<LoadError> Errors);
