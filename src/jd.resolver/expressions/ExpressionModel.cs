using jd.resolver.catalog;
using jd.resolver.environment;

namespace jd.resolver.expressions;

/// <summary>What a <see cref="Reference"/> reads at deploy time.</summary>
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

/// <summary>A string whose expressions all evaluated: <see cref="Value"/> is final.</summary>
public sealed record Resolved(string Value) : EvalResult;

/// <summary>Waits on <see cref="References"/>; evaluate <see cref="Original"/> again once their values are known.</summary>
public sealed record Pending(string Original, IReadOnlySet<Reference> References) : EvalResult;

/// <summary>Everything an evaluation may read. Filled by the caller; the evaluator performs no I/O.</summary>
/// <param name="Roles">Catalog role name to role-definition GUID.</param>
/// <param name="Naming">Catalog naming rules by resource kind.</param>
/// <param name="Environment">Source of <c>${env.…}</c> and of the environment name used by <c>name()</c>.</param>
/// <param name="Owner">Who the run belongs to: source of <c>${workload.…}</c> and of the owner name used by <c>name()</c>.</param>
/// <param name="Scope">The scope the expression is evaluated in.</param>
public sealed record ExpressionContext(
    IReadOnlyDictionary<string, string> Roles,
    IReadOnlyDictionary<string, NamingRule> Naming,
    EnvironmentDescriptor Environment,
    WorkloadOwner Owner,
    ExpressionScope Scope);
