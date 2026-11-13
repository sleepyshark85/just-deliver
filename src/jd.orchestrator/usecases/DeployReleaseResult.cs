using jd.resolver;
using jd.resolver.environment;
using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="DeployRelease"/> did.</summary>
/// <param name="Errors">Every problem found before the walk, each at its document and location.</param>
/// <param name="Problem">The refusal that stopped the run before the walk, or the record that could not be written after it; null when neither.</param>
/// <param name="Outcome"><see cref="Outcome.Succeeded"/> when every workload succeeded, IQ (if run) passed and the record (if any) was written.</param>
/// <param name="Environment">The environment; null when the run stopped before the walk.</param>
/// <param name="Set">The release set; null when the run stopped before the walk.</param>
/// <param name="Runs">The workloads walked, in deploy order; the last one failed when the walk did.</param>
/// <param name="Summary">How the workloads ended; null when the run stopped before the walk.</param>
/// <param name="Iq">The installation qualification; null for a preview, a failed walk or a run that stopped before it.</param>
/// <param name="Record">The record written; null for a preview, or when the run stopped before it or it could not be written.</param>
public sealed record DeployReleaseResult(
    IReadOnlyList<LoadError> Errors,
    Problem? Problem,
    Outcome Outcome,
    EnvironmentDescriptor? Environment,
    ReleaseSet? Set,
    IReadOnlyList<WorkloadRun> Runs,
    ReleaseSummary? Summary,
    IqResult? Iq,
    string? Record);
