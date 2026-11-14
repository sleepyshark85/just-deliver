using jd.resolver.expressions;

namespace jd.resolver.catalog;

/// <summary>
/// An IT Ops rule (docs/architecture/documents.md section 2, concept 10), bound from a policy document by the <c>policy.*</c> roles and the
/// criteria of its kind. A node-scope policy (its <see cref="Scope"/> has no value) sets and defaults config fields of every node at which each of
/// its <see cref="Criteria"/> holds; a workload-scope policy (<see cref="WorkloadScope"/>) sets and defaults fields of the runtime node and makes
/// every workload it matches require <see cref="Require"/>. Rules: docs/architecture/resolver.md (Policy scope).
/// </summary>
public sealed record Policy
{
    /// <summary>The scope a workload-scope policy names: the runtime node's name, as it applies to that node and its paths start with it.</summary>
    public const string WorkloadScope = ExpressionEvaluator.RuntimeNode;

    /// <summary>The catalog-relative file the policy is in.</summary>
    public required string Source { get; init; }

    /// <summary>The policy's name, unique in the catalog.</summary>
    public required string Name { get; init; }

    /// <summary>Why the policy exists.</summary>
    public required string Reason { get; init; }

    /// <summary>The scope the policy names; no value for node scope.</summary>
    public required PolicyField Scope { get; init; }

    /// <summary>The runtime a workload-scope policy names; not matched until a workload names its runtime.</summary>
    public required PolicyField Runtime { get; init; }

    /// <summary>Every criterion the policy's kind has, in schema order, with the value this policy compares (none when it gives none).</summary>
    public required IReadOnlyList<Criterion> Criteria { get; init; }

    /// <summary>Where the policy holds its criteria: the object holding its <see cref="Scope"/>; empty when it names no scope.</summary>
    public required string CriteriaLocation { get; init; }

    /// <summary>The config paths whose values win over every other layer.</summary>
    public required PolicyEntries Set { get; init; }

    /// <summary>The config paths whose values fill only fields no other layer gave.</summary>
    public required PolicyEntries Default { get; init; }

    /// <summary>The component every workload a workload-scope policy matches must require; no value when there is none.</summary>
    public required PolicyField Require { get; init; }
}
