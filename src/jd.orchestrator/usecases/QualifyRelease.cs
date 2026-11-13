using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>
/// <c>release qualify</c>: starts as <see cref="DeployRelease"/> does, a recorded run, then checks the deployment (IQ) without changing it, and writes
/// the record whether IQ passed or not (docs/architecture/application.md section 3; release-record.md sections 5 to 7).
/// </summary>
public static class QualifyRelease
{
    /// <summary>Runs <paramref name="input"/>; nothing is deployed.</summary>
    /// <param name="input">The set, the environment and the catalog.</param>
    /// <param name="connections">The records, the identity, the backend, the probe, the reader of live revisions, the clock and the templates.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<QualifyReleaseResult> RunAsync(QualifyReleaseInput input, Connections connections, CancellationToken cancellationToken)
    {
        var start = await ReleaseStart.StartAsync(new DeployReleaseInput(input.Set, input.Environment, input.Catalog, false, null), connections, cancellationToken);
        if (start.Run is not { } run)
        {
            return new(start.Errors, start.Problem, Outcome.Failed, null, null, null, null);
        }

        // Not a preview, so the start built the recording.
        var recording = run.Recording!;
        var iq = await recording.Qualifier.QualifyAsync(run.Graphs, cancellationToken);
        var (record, notWritten) = await RunRecord.WriteAsync(
            run, recording, ReleaseRecordFile.QualifyActivity,
            finished => ReleaseRecordBuilder.BuildQualify(run.Set, recording.SetSha256, run.Environment, run.Graphs, recording.Identity, run.Started, finished, iq),
            cancellationToken);
        return new([], notWritten, iq.Passed && notWritten is null ? Outcome.Succeeded : Outcome.Failed, run.Environment, run.Set, iq, record);
    }
}
