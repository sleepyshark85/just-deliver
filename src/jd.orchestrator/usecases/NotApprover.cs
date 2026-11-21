namespace jd.orchestrator.usecases;

/// <summary>The principal is not listed in the approvers of the environment's policy.</summary>
/// <param name="Approver">The principal's key (<c>DeployerIdentity.Principal</c>).</param>
/// <param name="Environment">The environment's name.</param>
public sealed record NotApprover(string Approver, string Environment) : Problem;
