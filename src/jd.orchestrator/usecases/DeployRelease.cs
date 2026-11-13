using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>
/// <c>release deploy</c>, and the walk of a promotion: starts the run (<see cref="ReleaseStart"/>), walks the workloads in deploy order, stopping at
/// the first that fails, or only previews them; then, for a recorded run, IQ if every workload deployed, and the record, written whether IQ passed or
/// not (docs/architecture/application.md section 3; release-record.md section 7).
/// </summary>
public static class DeployRelease
{
    /// <summary>Runs <paramref name="input"/>; nothing reaches the backend before every check of the start has passed.</summary>
    /// <param name="input">The set or the promotion, the environment, the catalog and whether to only preview.</param>
    /// <param name="connections">The records, the identity, the backend, the probe, the reader of live revisions, the clock and the templates.</param>
    /// <param name="progress">Told as each workload starts and as each of its nodes finishes.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="ArgumentException">The input has both a set and a promotion, neither, or a promotion that previews.</exception>
    public static async Task<DeployReleaseResult> RunAsync(DeployReleaseInput input, Connections connections, Action<Progress>? progress, CancellationToken cancellationToken)
    {
        var start = await ReleaseStart.StartAsync(input, connections, cancellationToken);
        if (start.Run is not { } run)
        {
            return new(start.Errors, start.Problem, Outcome.Failed, null, null, [], null, null, null);
        }

        var walk = run.Time.GetTimestamp();
        var runs = await run.Orchestrator.DeployReleaseAsync(
            run.Graphs, input.Preview, workload => progress?.Invoke(new WorkloadStarted(workload)), Walk.Reporting(progress), cancellationToken);
        var walked = run.Time.GetElapsedTime(walk);
        var deployed = runs.All(r => r.Run.Succeeded);
        if (run.Recording is not { } recording)
        {
            // A preview records nothing and qualifies nothing.
            return Walked(run, runs, deployed ? Outcome.Succeeded : Outcome.Failed, null, (null, null));
        }

        // IQ runs only after every workload deployed; a failed IQ leaves the deployment as it is.
        var iq = deployed ? await recording.Qualifier.QualifyAsync(run.Graphs, cancellationToken) : null;
        var written = await RunRecord.WriteAsync(
            run, recording, ReleaseRecordFile.DeployActivity,
            finished => ReleaseRecordBuilder.Build(run.Set, recording.SetSha256, run.Environment, run.Graphs, runs, recording.Identity, run.Started, finished, walked, iq),
            cancellationToken);
        return Walked(run, runs, iq is { Passed: true } && written.NotWritten is null ? Outcome.Succeeded : Outcome.Failed, iq, written);
    }

    private static DeployReleaseResult Walked(StartedRelease run, IReadOnlyList<WorkloadRun> runs, Outcome outcome, IqResult? iq, (string? Record, Problem? NotWritten) written) =>
        new([], written.NotWritten, outcome, run.Environment, run.Set, runs, ReleaseSummary.Of(run.Set, runs), iq, written.Record);
}
