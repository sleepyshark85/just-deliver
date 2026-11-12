namespace jd.orchestrator.usecases;

/// <summary>The environment carries an approval policy, so it is deployed to only by promotion (docs/architecture/approvals.md section 2).</summary>
/// <param name="Environment">The environment's name.</param>
public sealed record Gated(string Environment) : Problem;
