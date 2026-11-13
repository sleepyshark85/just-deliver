namespace jd.orchestrator.usecases;

/// <summary>The principal deployed the label where it was qualified, and the environment's policy requires approvers separate from the deployer.</summary>
/// <param name="Approver">The principal's object id.</param>
/// <param name="Label">The release label.</param>
/// <param name="QualifiedOn">The environment the principal deployed the label on.</param>
/// <param name="Environment">The gated environment's name.</param>
public sealed record NotSeparate(string Approver, string Label, string QualifiedOn, string Environment) : Problem;
