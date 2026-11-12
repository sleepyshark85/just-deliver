using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="Deploy"/> did.</summary>
/// <param name="Errors">Every problem found before the walk, each at its document and location.</param>
/// <param name="Problem">The refusal that stopped the run before the walk; null when none did.</param>
/// <param name="Outcome"><see cref="Outcome.Succeeded"/> when the walk succeeded, else <see cref="Outcome.Failed"/>.</param>
/// <param name="Run">The walk, its last node the failed one when it failed; null when the run stopped before it.</param>
public sealed record DeployResult(IReadOnlyList<LoadError> Errors, Problem? Problem, Outcome Outcome, RunReport? Run);
