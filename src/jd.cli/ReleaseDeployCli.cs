using System.Globalization;
using jd.core.bp;
using jd.orchestrator;
using jd.resolver.catalog;
using jd.resolver.environment;
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

    private sealed record Options(string Set, string Environment, string Catalog, bool DryRun);

    // args are the arguments after "release deploy".
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, IBackEndProvider? backend, Func<string, string?> environmentVariable, CancellationToken cancellationToken)
    {
        var (options, problem) = Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Cli.Usage);
            return UsageError;
        }

        problem = Arguments.Unreadable(options.Set, options.Environment, options.Catalog) ?? (backend is null ? Cli.BackendSettingsProblem(environmentVariable) : null);
        if (problem is not null)
        {
            stderr.WriteLine($"jd: {problem}");
            return UsageError;
        }

        try
        {
            return await DeployAsync(options, backend, environmentVariable, stdout, stderr, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return UsageError;
        }
    }

    private static async Task<int> DeployAsync(
        Options options, IBackEndProvider? backend, Func<string, string?> environmentVariable, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
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

        var orchestrator = Cli.CreateOrchestrator(templates, catalog, environment, backend, environmentVariable);
        var runs = await orchestrator.DeployReleaseAsync(
            graphs,
            options.DryRun,
            name => stdout.WriteLine($"workload {name}"),
            report => stdout.Write(DeployFormatter.Format(report)),
            cancellationToken);

        stdout.WriteLine(Summarize(set, environment.Name, runs, options.DryRun));
        var failed = runs.FirstOrDefault(r => !r.Run.Succeeded);
        if (failed is null)
        {
            return Success;
        }

        var node = failed.Run.Nodes[^1];
        stderr.WriteLine($"jd: stopped at {failed.Workload}, node {node.NodeId}: {node.Message}");
        return Invalid;
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
