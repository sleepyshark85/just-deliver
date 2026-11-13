using jd.core.identity;
using jd.resolver;
using jd.resolver.environment;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="Approve"/> did.</summary>
/// <param name="Errors">Every problem in the descriptor, each at its document and location.</param>
/// <param name="Problem">The refusal that stopped the approval; null when none did.</param>
/// <param name="Outcome">
/// <see cref="Outcome.Succeeded"/> when the approval was written, <see cref="Outcome.InvalidInput"/> when the label or the meaning was refused, else
/// <see cref="Outcome.Failed"/>.
/// </param>
/// <param name="Environment">The gated environment, its policy included; set exactly when the approval was written.</param>
/// <param name="Approver">The principal that approved; set exactly when the approval was written.</param>
/// <param name="Counted">How many approvals of the set count for the policy, this one included; 0 when none was written.</param>
/// <param name="Record">Where the approval was written; set exactly when it was.</param>
public sealed record ApproveResult(
    IReadOnlyList<LoadError> Errors, Problem? Problem, Outcome Outcome, EnvironmentDescriptor? Environment, DeployerIdentity? Approver, int Counted, string? Record);
