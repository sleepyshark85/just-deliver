using jd.resolver;
using jd.resolver.environment;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="Promote"/> did.</summary>
/// <param name="Errors">Every problem in the target's descriptor, each at its document and location.</param>
/// <param name="Problem">The refusal that stopped the promotion before its walk; null when none did.</param>
/// <param name="Outcome">
/// The walk's outcome once it ran; before it, <see cref="Outcome.InvalidInput"/> when the label was refused, else <see cref="Outcome.Failed"/>.
/// </param>
/// <param name="Environment">The gated environment, its policy included; set exactly when the walk ran.</param>
/// <param name="SetSha256">The hash of the set the checks judged; set exactly when the walk ran.</param>
/// <param name="Release">The walk of the kept set on the target, as <see cref="DeployRelease"/> reports one; null when the promotion stopped before it.</param>
public sealed record PromoteResult(
    IReadOnlyList<LoadError> Errors, Problem? Problem, Outcome Outcome, EnvironmentDescriptor? Environment, string? SetSha256, DeployReleaseResult? Release);
