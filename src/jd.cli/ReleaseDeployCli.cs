using System.Globalization;
using jd.orchestrator;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.environment;
using jd.resolver.release;

namespace jd.cli;

/// <summary>
/// <c>jd release deploy</c> and <c>jd release qualify</c>: read the set, the environment and the catalog, hand them to <see cref="DeployRelease"/> or
/// <see cref="QualifyRelease"/>, and write the walk as it happens, then the summary, the IQ verdict and the record. Reading and output; the exit code
/// from the <see cref="Outcome"/>.
/// </summary>
internal static class ReleaseDeployCli
{
    // The word a record that could not be written puts on what was done.
    private const string DeployedWord = "deployed";
    private const string CheckedWord = "checked";

    private sealed record Options(string Set, string Environment, string Catalog, bool DryRun);

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

        var set = await Cli.ReadDocumentAsync(options.Set, cancellationToken);
        var environment = await Cli.ReadDocumentAsync(options.Environment, cancellationToken);
        var catalog = await Cli.ReadCatalogAsync(options.Catalog, cancellationToken);
        return qualify
            ? PrintQualify(await QualifyRelease.RunAsync(new QualifyReleaseInput(set, environment, catalog), connections, cancellationToken), Command(qualify), stdout, stderr)
            : PrintDeploy(
                await DeployRelease.RunAsync(new DeployReleaseInput(set, environment, catalog, options.DryRun, null), connections, ShowWalk(stdout), cancellationToken),
                options.DryRun ? Previewed : DeployedOrUnchanged, Command(qualify), stdout, stderr);
    }

    // The walk of `jd promote` on the target, below the gate that refuses a direct deploy: the target and the catalog promote checked exist, and what it
    // settled. Its dispatch has no read-failure catch, so the walk has its own.
    public static Task<int> PromoteAsync(
        string environment, string catalog, Promotion promotion, TextWriter stdout, TextWriter stderr, Connections connections, CancellationToken cancellationToken) =>
        Cli.ExitOnReadFailureAsync(
            async () =>
            {
                var input = new DeployReleaseInput(
                    null, await Cli.ReadDocumentAsync(environment, cancellationToken), await Cli.ReadCatalogAsync(catalog, cancellationToken), false, promotion);
                return PrintDeploy(await DeployRelease.RunAsync(input, connections, ShowWalk(stdout), cancellationToken), DeployedOrUnchanged, Cli.PromoteCommand, stdout, stderr);
            },
            stderr);

    // done words what the walk did with the workloads that succeeded.
    private static int PrintDeploy(DeployReleaseResult result, Func<ReleaseSummary, string> done, string command, TextWriter stdout, TextWriter stderr)
    {
        if (result is not { Set: { } set, Environment: { } environment, Summary: { } summary })
        {
            return Refused(result.Errors, result.Problem, result.Outcome, command, stderr);
        }

        stdout.WriteLine(Summarize(set, environment, summary, done(summary)));
        if (result.Runs.FirstOrDefault(r => !r.Run.Succeeded) is { } failed)
        {
            var node = failed.Run.Nodes[^1];
            stderr.WriteLine($"jd: stopped at {failed.Workload}, node {node.NodeId}: {node.Message}");
        }

        if (result.Iq is { } iq)
        {
            ReportIq(set, environment, iq, stdout, stderr);
        }

        ReportRecord(result.Record, result.Problem, DeployedWord, stdout, stderr);
        return ExitCodes.Of(result.Outcome);
    }

    private static int PrintQualify(QualifyReleaseResult result, string command, TextWriter stdout, TextWriter stderr)
    {
        if (result is not { Set: { } set, Environment: { } environment, Iq: { } iq })
        {
            return Refused(result.Errors, result.Problem, result.Outcome, command, stderr);
        }

        ReportIq(set, environment, iq, stdout, stderr);
        ReportRecord(result.Record, result.Problem, CheckedWord, stdout, stderr);
        return ExitCodes.Of(result.Outcome);
    }

    // A run stopped before its walk: every error, or the refusal, on stderr.
    private static int Refused(IReadOnlyList<LoadError> errors, Problem? problem, Outcome outcome, string command, TextWriter stderr)
    {
        Cli.Print(errors, stderr);
        if (problem is not null)
        {
            stderr.WriteLine(Cli.Refusal(problem, command));
        }

        return ExitCodes.Of(outcome);
    }

    // The walk's lines: a heading as each workload starts, then its nodes as they finish.
    private static Action<Progress> ShowWalk(TextWriter stdout)
    {
        var nodes = Cli.ShowWalk(stdout);
        return progress =>
        {
            if (progress is WorkloadStarted started)
            {
                stdout.WriteLine($"workload {started.Workload}");
            }

            nodes(progress);
        };
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

    // The record written, or why it could not be, after what was done.
    private static void ReportRecord(string? record, Problem? notWritten, string done, TextWriter stdout, TextWriter stderr)
    {
        if (record is not null)
        {
            stdout.WriteLine($"record: {record}");
        }

        if (notWritten is RecordNotWritten { Reason: var reason })
        {
            stderr.WriteLine($"jd: the release was {done}, but its record could not be written: {reason}");
        }
    }

    private static string Previewed(ReleaseSummary summary) => $"{summary.Previewed.ToString(CultureInfo.InvariantCulture)} previewed";

    private static string DeployedOrUnchanged(ReleaseSummary summary) =>
        $"{summary.Deployed.ToString(CultureInfo.InvariantCulture)} deployed, {summary.Unchanged.ToString(CultureInfo.InvariantCulture)} unchanged";

    // done: what the walk did with the workloads that succeeded.
    private static string Summarize(ReleaseSet set, EnvironmentDescriptor environment, ReleaseSummary summary, string done)
    {
        var notStarted = summary.NotStarted.Count == 0
            ? "0 not started."
            : $"{summary.NotStarted.Count.ToString(CultureInfo.InvariantCulture)} not started ({string.Join(", ", summary.NotStarted)}).";
        return $"release {set.Label} on {environment.Name}: {done}, {summary.Failed.ToString(CultureInfo.InvariantCulture)} failed, {notStarted}";
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
            : (new Options(scanned.Positional[0], scanned.Environment, scanned.Catalog, scanned.DryRun), string.Empty);
    }
}
