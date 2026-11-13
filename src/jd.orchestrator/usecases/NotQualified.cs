namespace jd.orchestrator.usecases;

/// <summary>The label's set is not qualified on the environment the policy names (docs/architecture/approvals.md section 5).</summary>
/// <param name="Label">The release label.</param>
/// <param name="QualifiedOn">The environment the set must be qualified on.</param>
/// <param name="Reasons">Every reason it is not.</param>
public sealed record NotQualified(string Label, string QualifiedOn, IReadOnlyList<string> Reasons) : Problem;
