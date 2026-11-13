using jd.resolver;
using jd.resolver.environment;
using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="QualifyRelease"/> found.</summary>
/// <param name="Errors">Every problem found before IQ, each at its document and location.</param>
/// <param name="Problem">The refusal that stopped the run before IQ, or the record that could not be written after it; null when neither.</param>
/// <param name="Outcome"><see cref="Outcome.Succeeded"/> when IQ passed and the record was written.</param>
/// <param name="Environment">The environment; null when the run stopped before IQ.</param>
/// <param name="Set">The release set; null when the run stopped before IQ.</param>
/// <param name="Iq">The installation qualification; null when the run stopped before it.</param>
/// <param name="Record">The record written; null when the run stopped before it or it could not be written.</param>
public sealed record QualifyReleaseResult(
    IReadOnlyList<LoadError> Errors, Problem? Problem, Outcome Outcome, EnvironmentDescriptor? Environment, ReleaseSet? Set, IqResult? Iq, string? Record);
