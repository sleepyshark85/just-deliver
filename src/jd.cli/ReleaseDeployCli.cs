using System.Diagnostics;
using System.Globalization;
using jd.core.bp;
using jd.core.identity;
using jd.orchestrator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.release;

namespace jd.cli;

/// <summary>
/// <c>jd release deploy</c>: loads the set, resolves every workload and checks every graph against the templates, then lets the
/// orchestrator provision them in deploy order. Wiring and output only; exit codes as <see cref="Cli"/>.
/// </summary>
internal static class ReleaseDeployCli
{
    private const int Success = 0;
    private const int Invalid = 1;
    private const int UsageError = 2;

    private const string RecordsDirectoryVariable = "JD_RECORDS_DIR";

    private sealed record Options(string Set, string Environment, string Catalog, bool DryRun);

    // What a recorded run has before anything is deployed: where the records go, who deploys, and the kept set's hash.
    private sealed record Recording(string Directory, DeployerIdentity Identity, string SetSha256);

    // args are the arguments after "release deploy".
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, IBackEndProvider? backend, IRevisionProbe? probe, IDeployerIdentity? identity, TimeProvider time, Func<string, string?> environmentVariable,
        CancellationToken cancellationToken)
    {
        var (options, problem) = Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Cli.Usage);
            return UsageError;
        }

        // A preview deploys nothing, so it records nothing and needs no directory.
        var records = environmentVariable(RecordsDirectoryVariable);
        problem = Arguments.Unreadable(options.Set, options.Environment, options.Catalog) ?? (backend is null ? Cli.BackendSettingsProblem(environmentVariable) : null)
            ?? (options.DryRun || Directory.Exists(records) ? null : $"{RecordsDirectoryVariable} must name an existing directory where release records are kept (it is {(string.IsNullOrEmpty(records) ? "not set" : $"'{records}'")}).");
        if (problem is not null)
        {
            stderr.WriteLine($"jd: {problem}");
            return UsageError;
        }

        try
        {
            return await DeployAsync(options, options.DryRun ? null : records, backend, probe, identity, time, environmentVariable, stdout, stderr, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return UsageError;
        }
    }

    private static async Task<int> DeployAsync(
        Options options, string? recordsDirectory, IBackEndProvider? backend, IRevisionProbe? probe, IDeployerIdentity? identity, TimeProvider time, Func<string, string?> environmentVariable,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        var setResult = await ReleaseSetFile.LoadAsync(options.Set, cancellationToken);
        var catalogResult = await CatalogDirectory.LoadAsync(options.Catalog, cancellationToken);
        var environmentResult = await EnvironmentFile.LoadAsync(options.Environment, cancellationToken);
        if (setResult.Set is not { } set || catalogResult.Catalog is not { } catalog || environmentResult.Descriptor is not { } environment)
        {
            Cli.Print(setResult.Errors.Concat(Cli.CatalogErrors(catalogResult, options.Catalog)).Concat(environmentResult.Errors), stderr);
            return Invalid;
        }

        // Everything is resolved and checked before anything is created.
        var (graphs, errors) = await ReleaseResolver.ResolveAsync(options.Set, set, catalog, environment, cancellationToken);
        if (errors.Count > 0)
        {
            Cli.Print(errors, stderr);
            return Invalid;
        }

        if (await Cli.CheckTemplatesAsync(options.Catalog, graphs, stderr, cancellationToken) is not { } templates)
        {
            return Invalid;
        }

        // A recorded run knows who deploys, and that no other set was deployed under this label, before it calls the backend.
        Recording? recording = null;
        if (recordsDirectory is not null)
        {
            recording = await StartRecordingAsync(recordsDirectory, identity, environmentVariable, setResult.Bytes, set.Label, environment.Name, stderr, cancellationToken);
            if (recording is null)
            {
                return Invalid;
            }
        }

        var orchestrator = Cli.CreateOrchestrator(templates, catalog, environment, backend, environmentVariable, probe);
        var walk = Stopwatch.GetTimestamp();
        var runs = await orchestrator.DeployReleaseAsync(
            graphs,
            options.DryRun,
            name => stdout.WriteLine($"workload {name}"),
            report => stdout.Write(DeployFormatter.Format(report)),
            cancellationToken);
        var walked = Stopwatch.GetElapsedTime(walk);

        stdout.WriteLine(Summarize(set, environment.Name, runs, options.DryRun));
        var failed = runs.FirstOrDefault(r => !r.Run.Succeeded);
        if (failed is not null)
        {
            var node = failed.Run.Nodes[^1];
            stderr.WriteLine($"jd: stopped at {failed.Workload}, node {node.NodeId}: {node.Message}");
        }

        if (recording is not null && !await RecordAsync(recording, set, environment, graphs, runs, started, walked, time, stdout, stderr, cancellationToken))
        {
            return Invalid;
        }

        return failed is null ? Success : Invalid;
    }

    // Null, with the reason on stderr, when the run must not start: the principal cannot be named, the label cannot name a directory, or it holds another set.
    private static async Task<Recording?> StartRecordingAsync(
        string recordsDirectory, IDeployerIdentity? identity, Func<string, string?> environmentVariable, ReadOnlyMemory<byte> setBytes, string label, string environmentName,
        TextWriter stderr, CancellationToken cancellationToken)
    {
        DeployerIdentity principal;
        try
        {
            principal = await (identity ?? Cli.CreateDeployerIdentity(environmentVariable)).GetAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return null;
        }

        try
        {
            var (sha256, conflict) = await ReleaseRecordFile.KeepSetAsync(recordsDirectory, environmentName, label, setBytes, cancellationToken);
            if (conflict is null)
            {
                return new Recording(recordsDirectory, principal, sha256);
            }

            stderr.WriteLine($"jd: {conflict}");
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine($"jd: {ex.Message}");
        }

        return null;
    }

    // The deployment stays whether or not the record is written; false (and why) when it could not be.
    private static async Task<bool> RecordAsync(
        Recording recording, ReleaseSet set, EnvironmentDescriptor environment, IReadOnlyList<ResolvedGraph> graphs, IReadOnlyList<WorkloadRun> runs, DateTimeOffset started, TimeSpan walked,
        TimeProvider time, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var finished = time.GetUtcNow();
        var json = ReleaseRecordBuilder.Build(set, recording.SetSha256, environment, graphs, runs, recording.Identity, started, finished, walked);
        try
        {
            stdout.WriteLine($"record: {await ReleaseRecordFile.WriteAsync(recording.Directory, environment.Name, set.Label, finished, ReleaseRecordBuilder.DeployActivity, json, cancellationToken)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"jd: the release was deployed, but its record could not be written: {ex.Message}");
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

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        var (scanned, problem) = Arguments.Scan(args, 0);
        if (scanned is null)
        {
            return (null, problem);
        }

        if (scanned.Json)
        {
            return (null, "release deploy has no --json option.");
        }

        if (scanned.Positional.Count != 1)
        {
            return (null, "expected exactly one release set.");
        }

        return scanned.Environment is null || scanned.Catalog is null
            ? (null, "release deploy needs --env and --catalog.")
            : (new Options(scanned.Positional[0], scanned.Environment, scanned.Catalog, scanned.DryRun), string.Empty);
    }
}
