using jd.resolver;
using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="ShowRelease"/> read.</summary>
/// <param name="Errors">Every problem in the set, each at its document and location; empty when it is valid and unmodified.</param>
/// <param name="Outcome"><see cref="Outcome.Succeeded"/> when there are no errors, else <see cref="Outcome.Failed"/>.</param>
/// <param name="Set">The release set; set exactly when there are no errors.</param>
/// <param name="CreatedAt">The set's creation time as the set document holds it; set exactly when <paramref name="Set"/> is.</param>
public sealed record ShowReleaseResult(IReadOnlyList<LoadError> Errors, Outcome Outcome, ReleaseSet? Set, string? CreatedAt);
