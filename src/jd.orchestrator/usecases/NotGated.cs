namespace jd.orchestrator.usecases;

/// <summary>The environment carries no approval policy, so there is nothing to approve for it (docs/architecture/approvals.md section 4).</summary>
/// <param name="Environment">The environment's name.</param>
public sealed record NotGated(string Environment) : Problem;
