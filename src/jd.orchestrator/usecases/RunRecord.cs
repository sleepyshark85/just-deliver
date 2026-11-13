using jd.resolver.release;

namespace jd.orchestrator.usecases;

// The record of a recorded release run, written last: what the run did stays done whether or not it can be written.
internal static class RunRecord
{
    // The record built at the time the run finished, and its name; or why the records refused it.
    public static async Task<(string? Record, Problem? NotWritten)> WriteAsync(
        StartedRelease run, Recording recording, string activity, Func<DateTimeOffset, string> build, CancellationToken cancellationToken)
    {
        var finished = run.Time.GetUtcNow();
        var json = build(finished);
        try
        {
            return (await ReleaseRecordFile.WriteAsync(recording.Records, run.Environment.Name, run.Set.Label, finished, activity, json, cancellationToken), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, new RecordNotWritten(ex.Message));
        }
    }
}
