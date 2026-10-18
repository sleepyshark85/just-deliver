using System.Globalization;
using jd.core.bp;
using jd.core.identity;
using jd.core.revisions;
using jd.orchestrator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.release;

namespace jd.cli;

/// <summary>
/// <c>jd release deploy</c>: loads the set, resolves every workload and checks every graph against the templates, then lets the
/// orchestrator provision them in deploy order. <c>jd release qualify</c> starts the same way (a recorded run: records directory, identity,
/// <c>set.yaml</c>) and then checks the deployment (IQ) instead of changing it. Wiring and output only; exit codes as <see cref="Cli"/>.
/// </summary>
internal static class ReleaseDeployCli
{
    private const int Success = 0;
    private const int Invalid = 1;
    private const int UsageError = 2;

    // Promotion is set only by PromoteAsync, never by an argument: the run is the walk of a promotion that was judged already.
    private sealed record Options(string Set, string Environment, string Catalog, bool DryRun, bool Qualify)
    {
        public Promotion? Promotion { get; init; }
    }

    // What `jd promote` has settled before the walk: who runs it, and the hash of the kept set that the walk must read.
    internal sealed record Promotion(DeployerIdentity Principal, string SetSha256);

    // What a recorded run has before anything is deployed: where the records go, who deploys, and the kept set's hash.
    private sealed record Recording(string Directory, DeployerIdentity Identity, string SetSha256);

    // args are the arguments after "release deploy" or "release qualify" (qualify).
    public static async Task<int> RunAsync(
        string[] args, bool qualify, TextWriter stdout, TextWriter stderr, IBackEndProvider? backend, IRevisionProbe? probe, IDeployerIdentity? identity, ILiveRevisions? liveRevisions,
        TimeProvider time, Func<string, string?> environmentVariable, CancellationToken cancellationToken)
    {
        var (options, problem) = Parse(args, qualify);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Cli.Usage);
            return UsageError;
        }

        return await RunCheckedAsync(options, backend, probe, identity, liveRevisions, time, environmentVariable, stdout, stderr, cancellationToken);
    }

    // The walk of `jd promote` on the target, below the gate that refuses a direct deploy: the kept set, the target, the catalog, and what promote settled.
    public static Task<int> PromoteAsync(
        string keptSet, string environment, string catalog, Promotion promotion, TextWriter stdout, TextWriter stderr, IBackEndProvider? backend, IRevisionProbe? probe,
        ILiveRevisions? liveRevisions, TimeProvider time, Func<string, string?> environmentVariable, CancellationToken cancellationToken) =>
        RunCheckedAsync(new Options(keptSet, environment, catalog, false, false) { Promotion = promotion }, backend, probe, null, liveRevisions, time, environmentVariable, stdout, stderr, cancellationToken);

    private static async Task<int> RunCheckedAsync(
        Options options, IBackEndProvider? backend, IRevisionProbe? probe, IDeployerIdentity? identity, ILiveRevisions? liveRevisions, TimeProvider time,
        Func<string, string?> environmentVariable, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        // A preview deploys nothing, so it records nothing and needs no directory; a qualification records the check.
        var records = environmentVariable(Cli.RecordsDirectoryVariable);
        var problem = Arguments.Unreadable(options.Set, options.Environment, options.Catalog) ?? (backend is null ? Cli.BackendSettingsProblem(environmentVariable) : null)
            ?? (options.DryRun ? null : Cli.RecordsDirectoryProblem(records));
        if (problem is not null)
        {
            stderr.WriteLine($"jd: {problem}");
            return UsageError;
        }

        try
        {
            return await DeployAsync(options, options.DryRun ? null : records, backend, probe, identity, liveRevisions, time, environmentVariable, stdout, stderr, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return UsageError;
        }
    }

    private static async Task<int> DeployAsync(
        Options options, string? recordsDirectory, IBackEndProvider? backend, IRevisionProbe? probe, IDeployerIdentity? identity, ILiveRevisions? liveRevisions, TimeProvider time,
        Func<string, string?> environmentVariable, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
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

        if (Cli.RefuseType(catalog, environment, options.Environment, stderr))
        {
            return Invalid;
        }

        // A gated environment is deployed to only by promotion; this stops before the identity, the records or the backend is touched.
        if (!options.DryRun && options.Promotion is null && Cli.RefuseGated(environment, Command(options.Qualify), stderr))
        {
            return Invalid;
        }

        // The promotion judged the hash of the kept set; the bytes read now are the ones deployed, so they must be those.
        if (options.Promotion is { } promotion && ReleaseRecordFile.Sha256(setResult.Bytes.Span) != promotion.SetSha256)
        {
            stderr.WriteLine($"jd: promote: the kept set '{options.Set}' changed since it was checked (expected sha256 {promotion.SetSha256}).");
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
            recording = await StartRecordingAsync(recordsDirectory, options.Promotion?.Principal, identity, environmentVariable, setResult.Bytes, set.Label, environment.Name, stderr, cancellationToken);
            if (recording is null)
            {
                return Invalid;
            }
        }

        // Everything a run calls is built before any backend call, so a reader that cannot be built stops it before anything is deployed.
        // A preview records nothing and qualifies nothing, so it has no qualifier.
        Orchestrator orchestrator;
        Qualifier? qualifier = null;
        try
        {
            var provider = backend ?? Cli.CreatePulumiBackend(environmentVariable);
            orchestrator = Cli.CreateOrchestrator(templates, catalog, environment, provider, environmentVariable, probe);
            qualifier = recording is null ? null : new Qualifier(orchestrator, provider, templates, liveRevisions ?? Cli.CreateLiveRevisions(environmentVariable), time);
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return Invalid;
        }

        if (options.Qualify)
        {
            // recordsDirectory is null only for a --preview, which qualify refuses at parse time, so a qualification always has a recording and a qualifier here.
            return await QualifyAsync(recording!, set, environment, graphs, qualifier!, started, time, stdout, stderr, cancellationToken);
        }

        var walk = time.GetTimestamp();
        var runs = await orchestrator.DeployReleaseAsync(
            graphs,
            options.DryRun,
            name => stdout.WriteLine($"workload {name}"),
            report => stdout.Write(DeployFormatter.Format(report)),
            cancellationToken);
        var walked = time.GetElapsedTime(walk);

        stdout.WriteLine(Summarize(set, environment.Name, runs, options.DryRun));
        var failed = runs.FirstOrDefault(r => !r.Run.Succeeded);
        if (failed is not null)
        {
            var node = failed.Run.Nodes[^1];
            stderr.WriteLine($"jd: stopped at {failed.Workload}, node {node.NodeId}: {node.Message}");
        }

        // IQ runs only after every workload deployed; on a failure it is not run, and a failed IQ leaves the deployment as it is.
        IqResult? iq = null;
        if (qualifier is not null && failed is null)
        {
            iq = await qualifier.QualifyAsync(graphs, cancellationToken);
            ReportIq(set, environment, iq, stdout, stderr);
        }

        if (recording is not null)
        {
            var finished = time.GetUtcNow();
            var json = ReleaseRecordBuilder.Build(set, recording.SetSha256, environment, graphs, runs, recording.Identity, started, finished, walked, iq);
            if (!await RecordAsync(recording, set.Label, environment.Name, finished, ReleaseRecordFile.DeployActivity, json, "deployed", stdout, stderr, cancellationToken))
            {
                return Invalid;
            }
        }

        return failed is null && (iq?.Passed ?? true) ? Success : Invalid;
    }

    // Checks the deployment without touching it; the record is written whether IQ passed or not.
    private static async Task<int> QualifyAsync(
        Recording recording, ReleaseSet set, EnvironmentDescriptor environment, IReadOnlyList<ResolvedGraph> graphs, Qualifier qualifier, DateTimeOffset started, TimeProvider time,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var iq = await qualifier.QualifyAsync(graphs, cancellationToken);
        ReportIq(set, environment, iq, stdout, stderr);
        var finished = time.GetUtcNow();
        var json = ReleaseRecordBuilder.BuildQualify(set, recording.SetSha256, environment, graphs, recording.Identity, started, finished, iq);
        return await RecordAsync(recording, set.Label, environment.Name, finished, ReleaseRecordFile.QualifyActivity, json, "checked", stdout, stderr, cancellationToken) && iq.Passed ? Success : Invalid;
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

    // Null, with the reason on stderr, when the run must not start: the principal (known already, for a promotion) cannot be named, the label cannot name a directory, or it holds another set.
    private static async Task<Recording?> StartRecordingAsync(
        string recordsDirectory, DeployerIdentity? known, IDeployerIdentity? identity, Func<string, string?> environmentVariable, ReadOnlyMemory<byte> setBytes, string label, string environmentName,
        TextWriter stderr, CancellationToken cancellationToken)
    {
        if ((known ?? await Cli.GetIdentityAsync(identity, environmentVariable, stderr, cancellationToken)) is not { } principal)
        {
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

    // What was done stays whether or not the record is written; false (and why) when it could not be.
    private static async Task<bool> RecordAsync(
        Recording recording, string label, string environmentName, DateTimeOffset finished, string activity, string json, string done, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        try
        {
            stdout.WriteLine($"record: {await ReleaseRecordFile.WriteAsync(recording.Directory, environmentName, label, finished, activity, json, cancellationToken)}");
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

    private static string Command(bool qualify) => qualify ? "release qualify" : "release deploy";

    private static (Options? Options, string Problem) Parse(string[] args, bool qualify)
    {
        var command = Command(qualify);
        var (scanned, problem) = Arguments.Scan(args, 0);
        if (scanned is null)
        {
            return (null, problem);
        }

        if (scanned.Json)
        {
            return (null, $"{command} has no --json option.");
        }

        if (qualify && scanned.DryRun)
        {
            return (null, "release qualify has no --preview option; it never deploys.");
        }

        if (scanned.Positional.Count != 1)
        {
            return (null, "expected exactly one release set.");
        }

        return scanned.Environment is null || scanned.Catalog is null
            ? (null, $"{command} needs --env and --catalog.")
            : (new Options(scanned.Positional[0], scanned.Environment, scanned.Catalog, scanned.DryRun, qualify), string.Empty);
    }
}
