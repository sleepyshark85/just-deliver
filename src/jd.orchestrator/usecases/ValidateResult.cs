using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="Validate"/> found.</summary>
/// <param name="Errors">Every problem in the workload, each at its document and location; empty when it is valid.</param>
/// <param name="Outcome"><see cref="Outcome.Succeeded"/> when there are no errors, else <see cref="Outcome.Failed"/>.</param>
public sealed record ValidateResult(IReadOnlyList<LoadError> Errors, Outcome Outcome);
