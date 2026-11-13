namespace jd.orchestrator.usecases;

/// <summary>The label may not be promoted to the gated environment: its set is unknown or not qualified, or too few approvals count (docs/architecture/approvals.md section 5).</summary>
/// <param name="Label">The release label.</param>
/// <param name="Environment">The gated environment's name.</param>
/// <param name="Reasons">Every reason it may not.</param>
public sealed record NotPromotable(string Label, string Environment, IReadOnlyList<string> Reasons) : Problem;
