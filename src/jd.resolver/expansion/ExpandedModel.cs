using System.Collections.ObjectModel;
using jd.resolver.catalog;
using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.expansion;

/// <summary>A config value after evaluation. The tree mirrors the catalog node's config: strings are evaluated, everything else is carried over.</summary>
public abstract record ConfigValue;

/// <summary>A non-string scalar, copied from the catalog.</summary>
public sealed record ConfigScalar(JToken Value) : ConfigValue;

/// <summary>
/// A string, evaluated: <see cref="Resolved"/> or <see cref="Pending"/> on references. PartReads are the deployed parts' exports it read
/// (<c>&lt;name&gt;.&lt;export&gt;</c>, from the descriptor), which the preview shows.
/// </summary>
public sealed record ConfigText(EvalResult Result, IReadOnlySet<string> PartReads) : ConfigValue;

/// <summary>
/// A mapping of names to values. <paramref name="AsEntries"/> marks one declared with <c>fn::entries</c>: it stays a map
/// while policies address its keys, and is deployed as a list of <c>{name, value}</c> items sorted by name.
/// </summary>
public sealed record ConfigObject(IReadOnlyDictionary<string, ConfigValue> Properties, bool AsEntries = false) : ConfigValue;

/// <summary>A list from the catalog, each item evaluated; item order is kept, and the graph treats its strings as one provenance leaf.</summary>
public sealed record ConfigArray(IReadOnlyList<ConfigValue> Items) : ConfigValue;

/// <summary>A catalog node with its config evaluated, named as its part names it; policies apply to it next.</summary>
public sealed record ExpandedNode(string Name, string Template, NodeKind Kind, IReadOnlyDictionary<string, ConfigValue> Config);

/// <summary>
/// One requirement expanded from the component in <see cref="Source"/> (catalog-relative path of its file), or one part of an environment
/// run (id the substrate scope, no type or class, so no policy matching them applies).
/// </summary>
public sealed record ExpandedRequirement(
    string Id,
    string? Type,
    string? Class,
    string Source,
    IReadOnlyList<ExpandedNode> Nodes,
    IReadOnlyDictionary<string, EvalResult> Exports)
{
    /// <summary>Where <see cref="Source"/> holds each export, by name (<see cref="Part.ExportLocations"/>); empty for an environment run's part, which exports nothing here.</summary>
    internal IReadOnlyDictionary<string, string> ExportLocations { get; init; } = ReadOnlyDictionary<string, string>.Empty;
}

/// <summary>The workload's runtime nodes expanded from the runtime in <see cref="Source"/>, with its probe and release block.</summary>
public sealed record ExpandedRuntime(string Source, IReadOnlyList<ExpandedNode> Nodes, Probe? Probe, RuntimeRelease? Release);

/// <summary>Who the requirements belong to: a workload (it has a runtime), or an environment definition (substrate, no runtime).</summary>
public enum OwnerKind
{
    /// <summary>A workload definition: requirements and a runtime.</summary>
    Workload,

    /// <summary>An environment definition: the parts of its environment, no runtime.</summary>
    Environment,
}

/// <summary>An owner's requirements and runtime expanded, with what kept any of them from expanding.</summary>
public sealed record ExpansionResult
{
    /// <summary>Whether the requirements are a workload's or an environment definition's.</summary>
    public required OwnerKind Kind { get; init; }

    /// <summary>The workload's (or definition's) file, where its requirements and variables were written; errors about them name it.</summary>
    public required string OwnerFile { get; init; }

    /// <summary>Who the run belongs to; carried on for policy evaluation.</summary>
    public required WorkloadOwner Owner { get; init; }

    /// <summary>Requirements that expanded cleanly; a requirement with any error is left out.</summary>
    public required IReadOnlyList<ExpandedRequirement> Requirements { get; init; }

    /// <summary>The type of every requirement the owner declares, including those that failed to expand.</summary>
    public required IReadOnlySet<string> DeclaredTypes { get; init; }

    /// <summary>The runtime nodes of a workload; null for an environment definition, and when the runtime failed to expand.</summary>
    public required ExpandedRuntime? Runtime { get; init; }

    /// <summary>Everything that kept a requirement from expanding.</summary>
    public required IReadOnlyList<LoadError> Errors { get; init; }

    /// <summary>An environment run's parts: their exports (name to expression) by part name; empty for a workload.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parts { get; init; }

    /// <summary>Where <see cref="OwnerFile"/> holds the workload's requirements list, or would; empty for an environment run, which has none.</summary>
    internal string RequirementsLocation { get; init; } = string.Empty;

    /// <summary>Where <see cref="OwnerFile"/> holds the workload's variables, or would; empty for an environment run, which has none.</summary>
    internal string VariablesLocation { get; init; } = string.Empty;
}
