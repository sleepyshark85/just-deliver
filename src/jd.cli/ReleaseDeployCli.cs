using System.Globalization;
using jd.orchestrator;
using jd.orchestrator.usecases;
using jd.resolver.environment;
using jd.resolver.release;

namespace jd.cli;

/// <summary>
/// <c>jd release deploy</c>: reads the set, the environment and the catalog, lets <see cref="ReleaseStart"/> load, check and resolve them and build what
/// the run calls, then lets the orchestrator provision the workloads in deploy order. <c>jd release qualify</c> starts the same way (a recorded run:
/// identity, <c>set.yaml</c>) and then checks the deployment (IQ) instead of changing it. Reading and output; exit codes from <see cref="ExitCodes"/>.
/// </summary>
internal static class ReleaseDeployCli
{
    private sealed record Options(string Set, string Environment, string Catalog, bool DryRun, bool Qualify);

    // args are the arguments after "release deploy" or "release qualify" (qualify).
    public static async Task<int> RunAsync(string[] args, bool qualify, TextWriter stdout, TextWriter stderr, Connections connections, CancellationToken cancellationToken)
    {
        var (options, problem) = Parse(args, qualify);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Cli.Usage);
            return ExitCodes.UsageError;
        }

        if (Arguments.Unreadable(options.Set, options.Environment, options.Catalog) is { } unreadable)
        {
            stderr.WriteLine($"jd: {unreadable}");
            return ExitCodes.UsageError;
        }

        var input = new ReleaseStartInput(
            await Cli.ReadDocumentAsync(options.Set, cancellationToken), await Cli.ReadDocumentAsync(options.Environment, cancellationToken),
            await Cli.ReadCatalogAsync(options.Catalog, cancellationToken), options.DryRun, null);
        if (await StartAsync(input, Command(qualify), connections, stderr, cancellationToken) is not { } run)
        {
            return ExitCodes.Failure;
        }

        return qualify ? await QualifyAsync(run, stdout, stderr, cancellationToken) : await DeployAsync(run, options.DryRun, stdout, stderr, cancellationToken);
    }

    // The walk of `jd promote` on the target, below the gate that refuses a direct deploy: the target and the catalog promote checked exist, and what it
    // settled. Its dispatch has no read-failure catch, so the walk has its own.
    public static Task<int> PromoteAsync(
        string environment, string catalog, Promotion promotion, TextWriter stdout, TextWriter stderr, Connections connections, CancellationToken cancellationToken) =>
        Cli.ExitOnReadFailureAsync(
            async () =>
            {
                var input = new ReleaseStartInput(
                    null, await Cli.ReadDocumentAsync(environment, cancellationToken), await Cli.ReadCatalogAsync(catalog, cancellationToken), false, promotion);
                return await StartAsync(input, Cli.PromoteCommand, connections, stderr, cancellationToken) is { } run
                    ? await DeployAsync(run, false, stdout, stderr, cancellationToken)
                    : ExitCodes.Failure;
            },
            stderr);

    // The started run; null, with every error or the refusal on stderr, when the start refused it.
    private static async Task<StartedRelease?> StartAsync(ReleaseStartInput input, string command, Connections connections, TextWriter stderr, CancellationToken cancellationToken)
    {
        var result = await ReleaseStart.StartAsync(input, connections, cancellationToken);
        Cli.Print(result.Errors, stderr);
        if (result.Problem is { } problem)
        {
            stderr.WriteLine(Cli.Refusal(problem, command));
        }

        return result.Run;
    }

    private static async Task<int> DeployAsync(StartedRelease run, bool preview, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var (set, environment, graphs, orchestrator, recording, time, started) = run;
        var walk = time.GetTimestamp();
        var runs = await orchestrator.DeployReleaseAsync(
            graphs,
            preview,
            name => stdout.WriteLine($"workload {name}"),
            report => stdout.Write(DeployFormatter.Format(report)),
            cancellationToken);
        var walked = time.GetElapsedTime(walk);

        stdout.WriteLine(Summarize(set, environment.Name, runs, preview));
        var failed = runs.FirstOrDefault(r => !r.Run.Succeeded);
        if (failed is not null)
        {
            var node = failed.Run.Nodes[^1];
            stderr.WriteLine($"jd: stopped at {failed.Workload}, node {node.NodeId}: {node.Message}");
        }

        // IQ runs only after every workload deployed; on a failure it is not run, and a failed IQ leaves the deployment as it is.
        IqResult? iq = null;
        if (recording is not null && failed is null)
        {
            iq = await recording.Qualifier.QualifyAsync(graphs, cancellationToken);
            ReportIq(set, environment, iq, stdout, stderr);
        }

        if (recording is not null)
        {
            var finished = time.GetUtcNow();
            var json = ReleaseRecordBuilder.Build(set, recording.SetSha256, environment, graphs, runs, recording.Identity, started, finished, walked, iq);
            if (!await RecordAsync(recording, set.Label, environment.Name, finished, ReleaseRecordFile.DeployActivity, json, "deployed", stdout, stderr, cancellationToken))
            {
                return ExitCodes.Failure;
            }
        }

        return failed is null && (iq?.Passed ?? true) ? ExitCodes.Success : ExitCodes.Failure;
    }

    // Checks the deployment without touching it; the record is written whether IQ passed or not.
    private static async Task<int> QualifyAsync(StartedRelease run, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var (set, environment, graphs, _, recording, time, started) = run;
        // A qualification is never a preview (parse refuses --preview), so its start built the recording.
        var iq = await recording!.Qualifier.QualifyAsync(graphs, cancellationToken);
        ReportIq(set, environment, iq, stdout, stderr);
        var finished = time.GetUtcNow();
        var json = ReleaseRecordBuilder.BuildQualify(set, recording.SetSha256, environment, graphs, recording.Identity, started, finished, iq);
        return await RecordAsync(recording, set.Label, environment.Name, finished, ReleaseRecordFile.QualifyActivity, json, "checked", stdout, stderr, cancellationToken) && iq.Passed
            ? ExitCodes.Success
            : ExitCodes.Failure;
    }

    // The verdict on stdout, and every failed check on stderr.
    private static void ReportIq(ReleaseSet set, EnvironmentDescriptor environment, IqResult iq, TextWriter stdout, TextWriter stderr)
    {
        var failures = iq.Checks.Where(c => !c.Passed).ToList();
        stdout.WriteLine($"release {set.Label} on {environment.Name}: IQ {(iq.Passed ? "passed" : "failed")}, {iq.Checks.Count.ToString(CultureInfo.InvariantCulture)} checks, {failures.Count.ToString(CultureInfo.InvariantCulture)} failed.");
        foreach (var failure in failures)
        {
            stderr.WriteLine($"jd: IQ failed: {failure.Workload}: {failure.Failure}");
        }
    }

    // What was done stays whether or not the record is written; false (and why) when it could not be.
    private static async Task<bool> RecordAsync(
        Recording recording, string label, string environmentName, DateTimeOffset finished, string activity, string json, string done, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        try
        {
            stdout.WriteLine($"record: {await ReleaseRecordFile.WriteAsync(recording.Records, environmentName, label, finished, activity, json, cancellationToken)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"jd: the release was {done}, but its record could not be written: {ex.Message}");
            return false;
        }
    }

    // A workload is "deployed" when one of its nodes changed, otherwise "unchanged"; a preview changes nothing, so its workloads are "previewed".
    private static string Summarize(ReleaseSet set, string environment, IReadOnlyList<WorkloadRun> runs, bool dryRun)
    {
        var succeeded = runs.Where(r => r.Run.Succeeded).ToList();
        var deployed = succeeded.Count(r => r.Run.Nodes.Any(n => n.Outcome == NodeOutcome.Deployed));
        var done = dryRun
            ? $"{succeeded.Count.ToString(CultureInfo.InvariantCulture)} previewed"
            : $"{deployed.ToString(CultureInfo.InvariantCulture)} deployed, {(succeeded.Count - deployed).ToString(CultureInfo.InvariantCulture)} unchanged";
        var failed = runs.Count - succeeded.Count;
        var notStarted = set.Order.Skip(runs.Count).ToList();
        return $"release {set.Label} on {environment}: {done}, {failed.ToString(CultureInfo.InvariantCulture)} failed, "
            + (notStarted.Count == 0 ? "0 not started." : $"{notStarted.Count.ToString(CultureInfo.InvariantCulture)} not started ({string.Join(", ", notStarted)}).");
    }

    private static string Command(bool qualify) => $"{Cli.ReleaseCommand} {(qualify ? ReleaseCli.QualifyCommand : ReleaseCli.DeployCommand)}";

    private static (Options? Options, string Problem) Parse(string[] args, bool qualify)
    {
        var command = Command(qualify);
        var (scanned, problem) = Arguments.Scan(args);
        if (scanned is null)
        {
            return (null, problem);
        }

        if (scanned.Json)
        {
            return (null, $"{command} has no {Arguments.JsonOption} option.");
        }

        if (qualify && scanned.DryRun)
        {
            return (null, $"{command} has no {Arguments.PreviewOption} option; it never deploys.");
        }

        if (scanned.Positional.Count != 1)
        {
            return (null, "expected exactly one release set.");
        }

        return scanned.Environment is null || scanned.Catalog is null
            ? (null, $"{command} needs {Arguments.EnvOption} and {Arguments.CatalogOption}.")
            : (new Options(scanned.Positional[0], scanned.Environment, scanned.Catalog, scanned.DryRun, qualify), string.Empty);
    }
}
