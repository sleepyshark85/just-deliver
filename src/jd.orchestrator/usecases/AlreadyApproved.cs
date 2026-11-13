namespace jd.orchestrator.usecases;

/// <summary>The principal already approved the label's set for the environment.</summary>
/// <param name="Approver">The principal's object id.</param>
/// <param name="Label">The release label.</param>
/// <param name="Environment">The gated environment's name.</param>
public sealed record AlreadyApproved(string Approver, string Label, string Environment) : Problem;
