using jd.resolver.catalog;
using jd.resolver.environment;

namespace jd.resolver.expressions;

public enum ReferenceKind
{
    /// <summary>An output of a graph node: <c>${node.output}</c>; the runtime node is the node named <c>runtime</c>.</summary>
    Node,

    /// <summary>An export of a workload requirement: <c>${resource.id.export}</c>.</summary>
    Resource,
}

/// <summary>A value that exists only at deploy time. <see cref="Target"/> is the node name or requirement id.</summary>
public sealed record Reference(ReferenceKind Kind, string Target, string Output);

/// <summary>The outcome of evaluating one string: <see cref="Resolved"/> or <see cref="Pending"/>.</summary>
public abstract record EvalResult;

public sealed record Resolved(string Value) : EvalResult;

/// <summary>Waits on <see cref="References"/>; evaluate <see cref="Original"/> again once their values are known.</summary>
public sealed record Pending(string Original, IReadOnlySet<Reference> References) : EvalResult;

/// <summary>Everything an evaluation may read. Filled by the caller; the evaluator performs no I/O.</summary>
/// <param name="Roles">Catalog role name to role-definition GUID.</param>
/// <param name="Naming">Catalog naming rules by resource kind.</param>
/// <param name="Environment">Source of <c>${env.…}</c> and of the environment name used by <c>name()</c>.</param>
/// <param name="WorkloadName">The workload's <c>metadata.name</c>.</param>
/// <param name="WorkloadTeam">The workload's <c>metadata.team</c>.</param>
/// <param name="WorkloadImage">The workload's <c>container.image</c>; null where there is none (an environment definition).</param>
/// <param name="WorkloadPort">The first port of the workload's <c>container.ports</c>; null when it declares none.</param>
/// <param name="CurrentId">The requirement's effective id, or the node name for nodes not tied to a requirement.</param>
/// <param name="NodeNames">Nodes in scope for <c>${node.output}</c>.</param>
/// <param name="KnownOutputs">Outputs already known; a reference found here resolves instead of staying pending.</param>
public sealed record ExpressionContext(
    IReadOnlyDictionary<string, string> Roles,
    IReadOnlyDictionary<string, NamingRule> Naming,
    EnvironmentDescriptor Environment,
    string WorkloadName,
    string WorkloadTeam,
    string CurrentId,
    IReadOnlySet<string> NodeNames,
    IReadOnlyDictionary<Reference, string> KnownOutputs,
    string? WorkloadImage,
    int? WorkloadPort);
