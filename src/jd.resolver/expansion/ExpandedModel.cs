using jd.resolver.catalog;
using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.expansion;

/// <summary>A config value after evaluation. The tree mirrors the mapping's config: strings are evaluated, everything else is carried over.</summary>
public abstract record ConfigValue;

/// <summary>A non-string scalar, copied from the catalog.</summary>
public sealed record ConfigScalar(JToken Value) : ConfigValue;

/// <summary>A string, evaluated: <see cref="Resolved"/> or <see cref="Pending"/> on references.</summary>
public sealed record ConfigText(EvalResult Result) : ConfigValue;

public sealed record ConfigObject(IReadOnlyDictionary<string, ConfigValue> Properties) : ConfigValue;

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

/// <param name="WorkloadName">The workload's <c>metadata.name</c>, carried on for policy evaluation.</param>
/// <param name="WorkloadTeam">The workload's <c>metadata.team</c>.</param>
/// <param name="Requirements">Requirements that expanded cleanly; a requirement with any error is left out.</param>
/// <param name="Errors">Everything that kept a requirement from expanding.</param>
public sealed record ExpansionResult(string WorkloadName, string WorkloadTeam, IReadOnlyList<ExpandedRequirement> Requirements, IReadOnlyList<LoadError> Errors);
