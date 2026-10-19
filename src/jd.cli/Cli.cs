using Azure.Core;
using jd.azure;
using jd.bp.pulumi;
using jd.core.bp;
using jd.core.identity;
using jd.core.records;
using jd.core.revisions;
using jd.orchestrator;
using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.workload;
using Microsoft.Extensions.DependencyInjection;

namespace jd.cli;

/// <summary>
/// The composition root: parses arguments, wires the validator, the resolver, the orchestrator and the Pulumi backend, prints
/// results and returns the exit code (0 success, 1 validation, resolution or deployment errors, 2 usage errors). Holds no resolution logic.
/// </summary>
public static class Cli
{
    private const int Success = 0;
    private const int Invalid = 1;
    private const int UsageError = 2;

    internal const string Usage = """
        Usage:
          jd validate <workload.yaml>
          jd preview <workload.yaml> --env <environment.yaml> --catalog <dir> [--json]
          jd release create <manifest.yaml> --out <release-set.yaml>
          jd release show <release-set.yaml>
          jd release deploy <release-set.yaml> --env <environment.yaml> --catalog <dir> [--preview]
          jd release qualify <release-set.yaml> --env <environment.yaml> --catalog <dir>
          jd approve <label> --env <environment.yaml> --meaning <text>
          jd promote <label> --to <environment.yaml> --catalog <dir>
          jd deploy <workload.yaml> --env <environment.yaml> --catalog <dir> [--preview]
          jd env up <definition.yaml> --catalog <dir> --out <descriptor.yaml> [--base <descriptor.yaml>] [--force]
          jd --help

        validate  checks a workload against its schema and rules.
        preview   validates the workload, then prints the resolved graph (--json: as stable JSON).
        release   create writes an immutable release set (pinned workloads, deploy order) from a manifest; show prints one;
                  deploy resolves every workload of a set, then deploys them in deploy order, stopping at the first failure, and writes a release record
                  (not with --preview); qualify re-checks a deployed set without deploying (no drift, the pinned image on every revision that serves traffic) and writes a record.
        approve   records that the signed-in principal approves the kept set of <label> for a gated environment (its descriptor has an approval block);
                  refused unless the set is qualified on the policy's qualifiedOn environment and the principal may approve it; --meaning (1 to 500 characters) says what the approval asserts.
        promote   deploys the kept set of <label> to a gated environment (--to: its descriptor): refused unless the set is qualified on the policy's qualifiedOn environment and enough
                  principals approved it; then the same walk as release deploy, with the same set bytes, and IQ.
        deploy    provisions the infrastructure nodes in order, then releases the runtime: dark revision, grants, probe, traffic shift (--preview: only shows the changes; nothing is created).
        env up    provisions an environment's substrate from its definition (in the definition's region) and writes the environment descriptor from the outputs.
                  --base: the descriptor of the layer below (the new values are added to it); --force: overwrite an existing --out.
        Gated (deploy, release deploy, release qualify): an environment whose descriptor has an approval block refuses them (exit 1; --preview is allowed): it is deployed to only by `jd promote`.
        Data (deploy, release deploy, release qualify, approve, promote, env up): the deployment state, its key and the release records are kept in jd's data
                  directory: jd in the local application data folder (~/.local/share/jd on Linux), or JD_DATA_DIR. PULUMI_HOME, JD_SCRATCH_DIR optional.
        Identity: the Azure CLI sign-in (az login), or the service principal in ARM_TENANT_ID, ARM_CLIENT_ID and ARM_CLIENT_SECRET when all three are set;
                  it deploys, and the records name it as the principal that deployed or approved.
        Exit codes: 0 success, 1 validation, resolution or deployment errors, 2 usage errors.

        """;

    private abstract record Options(string Workload);

    private sealed record ValidateOptions(string Workload) : Options(Workload);

    private abstract record ResolveOptions(string Workload, string Environment, string Catalog) : Options(Workload);

    private sealed record PreviewOptions(string Workload, string Environment, string Catalog, bool Json) : ResolveOptions(Workload, Environment, Catalog);

    private sealed record DeployOptions(string Workload, string Environment, string Catalog, bool DryRun) : ResolveOptions(Workload, Environment, Catalog);

    /// <summary>Runs the command in <paramref name="args"/>, writing results to <paramref name="stdout"/> and problems to <paramref name="stderr"/>; returns the exit code.</summary>
    /// <remarks>The last six parameters are test seams; each null means the real one.</remarks>
    /// <param name="args">The command line, without the program name.</param>
    /// <param name="stdout">Where results are written.</param>
    /// <param name="stderr">Where problems and usage are written.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="backend">The backend a deploy uses (null: the Pulumi backend on the state in jd's data directory).</param>
    /// <param name="environmentVariable">Reads the environment variables (null: the process environment).</param>
    /// <param name="probe">Checks a new revision is healthy (null: an HTTP GET).</param>
    /// <param name="identity">Names the deploying principal (null: the Azure CLI sign-in or the ARM_* variables).</param>
    /// <param name="time">Stamps the release record (null: the system clock).</param>
    /// <param name="liveRevisions">Reads what runs (null: Azure, with the same variables).</param>
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default, IBackEndProvider? backend = null, Func<string, string?>? environmentVariable = null,
        IRevisionProbe? probe = null, IDeployerIdentity? identity = null, TimeProvider? time = null, ILiveRevisions? liveRevisions = null)
    {
        environmentVariable ??= System.Environment.GetEnvironmentVariable;
        if (args.Contains("--help") || args.Contains("-h"))
        {
            stdout.Write(Usage);
            return Success;
        }

        if (args.FirstOrDefault() == "release")
        {
            return await ReleaseCli.RunAsync(args[1..], stdout, stderr, backend, probe, identity, liveRevisions, time ?? TimeProvider.System, environmentVariable, cancellationToken);
        }

        if (args.FirstOrDefault() == "approve")
        {
            return await ApproveCli.RunAsync(args[1..], stdout, stderr, identity, time ?? TimeProvider.System, environmentVariable, cancellationToken);
        }

        if (args.FirstOrDefault() == "promote")
        {
            return await PromoteCli.RunAsync(args[1..], stdout, stderr, backend, probe, identity, liveRevisions, time ?? TimeProvider.System, environmentVariable, cancellationToken);
        }

        if (args.FirstOrDefault() == "env")
        {
            return await EnvCli.RunAsync(args[1..], stdout, stderr, backend, environmentVariable, cancellationToken);
        }

        var (options, problem) = Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Usage);
            return UsageError;
        }

        var unreadable = Arguments.Unreadable(options.Workload, (options as ResolveOptions)?.Environment, (options as ResolveOptions)?.Catalog);
        if (unreadable is not null)
        {
            stderr.WriteLine($"jd: {unreadable}");
            return UsageError;
        }

        try
        {
            return await RunCommandAsync(options, backend, probe, environmentVariable, stdout, stderr, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The inputs exist (checked above), so this is a permission or read failure; .NET's message names the file.
            stderr.WriteLine($"jd: {ex.Message}");
            return UsageError;
        }
    }

    private static async Task<int> RunCommandAsync(Options options, IBackEndProvider? backend, IRevisionProbe? probe, Func<string, string?> environmentVariable, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var loaded = await WorkloadFile.LoadAsync(options.Workload, cancellationToken);
        if (loaded.Value is not { } workload)
        {
            Print(loaded.Errors, stderr);
            return Invalid;
        }

        if (options is not ResolveOptions resolve)
        {
            stdout.WriteLine($"{options.Workload}: valid.");
            return Success;
        }

        var catalogResult = await CatalogDirectory.LoadAsync(resolve.Catalog, cancellationToken);
        var environmentResult = await EnvironmentParser.LoadAsync(resolve.Environment, cancellationToken);
        if (catalogResult.Value is not { } catalog || environmentResult.Value is not { } environment)
        {
            Print(CatalogErrors(catalogResult, resolve.Catalog).Concat(environmentResult.Errors), stderr);
            return Invalid;
        }

        if (RefuseType(catalog, environment, resolve.Environment, stderr))
        {
            return Invalid;
        }

        if (resolve is DeployOptions { DryRun: false } && RefuseGated(environment, "deploy", stderr))
        {
            return Invalid;
        }

        var graph = Resolver.Resolve(workload, options.Workload, catalog, environment);
        if (graph.Errors.Count > 0)
        {
            Print(graph.Errors, stderr);
            return Invalid;
        }

        if (resolve is DeployOptions deploy)
        {
            return await DeployAsync(deploy, graph, catalog, environment, backend, probe, environmentVariable, stdout, stderr, cancellationToken);
        }

        stdout.Write(resolve is PreviewOptions { Json: true } ? GraphJson.Serialize(graph) : PreviewFormatter.Format(graph));
        return Success;
    }

    private static async Task<int> DeployAsync(
        DeployOptions options, ResolvedGraph graph, Catalog catalog, EnvironmentDescriptor environment, IBackEndProvider? backend, IRevisionProbe? probe,
        Func<string, string?> environmentVariable, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken) =>
        (await ProvisionAsync(options.Catalog, graph, catalog, environment, backend, environmentVariable, options.DryRun, stdout, stderr, probe, cancellationToken)).Code;

    // An environment whose descriptor carries an approval policy takes deploys only through `jd promote` (docs/architecture/approvals.md section 2). The check reads
    // the block, not the type. Run before the identity, the records or the backend is touched; a preview creates nothing, so callers do not ask it.
    internal static bool RefuseGated(EnvironmentDescriptor environment, string command, TextWriter stderr)
    {
        if (environment.Approval is null)
        {
            return false;
        }

        stderr.WriteLine($"jd: {command}: environment '{environment.Name}' is gated by an approval policy: use `jd promote`.");
        return true;
    }

    // An environment's type must be the catalog's, and a type that requires an approval block must carry it (security boundary 13). Prints what is wrong, naming the file.
    internal static bool RefuseType(Catalog catalog, EnvironmentDescriptor environment, string file, TextWriter stderr)
    {
        if (EnvironmentTypeCheck.Check(catalog, environment.Type, environment.Approval, file) is not { Count: > 0 } errors)
        {
            return false;
        }

        Print(errors, stderr);
        return true;
    }

    // Catalog errors name files relative to the catalog root; the person needs the path they can open.
    // An error about the catalog as a whole names no real file and is left as it is.
    internal static IEnumerable<LoadError> CatalogErrors(LoadResult<Catalog> result, string catalogDirectory) =>
        result.Errors.Select(e => Path.Combine(catalogDirectory, e.File) is var full && File.Exists(full) ? e with { File = full } : e);

    // Templates live in the catalog's templates directory; the graphs must fit them before anything is created.
    // Null when they do not (the misfits are printed once).
    internal static async Task<TemplateLibrary?> CheckTemplatesAsync(string catalogDirectory, IEnumerable<ResolvedGraph> graphs, TextWriter stderr, CancellationToken cancellationToken)
    {
        var templates = await TemplateLibrary.LoadAsync(Path.Combine(catalogDirectory, CatalogDirectory.TemplatesDirectory), cancellationToken);
        var misfits = graphs.SelectMany(templates.Check).Distinct().ToList(); // unreadable templates are repeated by every graph's check
        if (misfits.Count == 0)
        {
            return templates;
        }

        Print(misfits, stderr);
        return null;
    }

    // The probe judges the revision's own answer, so a redirect is a failed probe, not a hop to some other host.
    private static IRevisionProbe HttpProbe() => new HttpRevisionProbe(HttpRevisionProbe.CreateClient());

    // The one place the default probe (an HTTP GET) is chosen; a substrate never uses it.
    internal static Orchestrator CreateOrchestrator(
        TemplateLibrary templates, Catalog catalog, EnvironmentDescriptor environment, IBackEndProvider? backend, Func<string, string?> environmentVariable, IRevisionProbe? probe) =>
        new(backend ?? CreatePulumiBackend(environmentVariable), templates, catalog, environment, probe ?? HttpProbe());

    internal static async Task<(int Code, RunReport? Run)> ProvisionAsync(
        string catalogDirectory, ResolvedGraph graph, Catalog catalog, EnvironmentDescriptor environment, IBackEndProvider? backend,
        Func<string, string?> environmentVariable, bool dryRun, TextWriter stdout, TextWriter stderr, IRevisionProbe? probe, CancellationToken cancellationToken)
    {
        if (await CheckTemplatesAsync(catalogDirectory, [graph], stderr, cancellationToken) is not { } templates)
        {
            return (Invalid, null);
        }

        var orchestrator = CreateOrchestrator(templates, catalog, environment, backend, environmentVariable, probe);
        void Show(NodeReport report) => stdout.Write(DeployFormatter.Format(report));
        var run = dryRun
            ? await orchestrator.PreviewAsync(graph, Show, cancellationToken)
            : await orchestrator.DeployAsync(graph, Show, cancellationToken);
        if (!run.Succeeded)
        {
            stderr.WriteLine($"jd: stopped at {run.Nodes[^1].NodeId}: {run.Nodes[^1].Message}");
            return (Invalid, run);
        }

        return (Success, run);
    }

    // The release, qualification and approval records, in jd's data directory.
    internal static IRecordStore Records(Func<string, string?> environmentVariable) => DataDirectory.Resolve(environmentVariable).Records;

    // The principal the run authenticates with: the ARM_* service principal Pulumi uses too, or the Azure CLI sign-in. Throws InvalidOperationException naming the unset ones of a partial ARM_* set.
    internal static IDeployerIdentity CreateDeployerIdentity(Func<string, string?> environmentVariable) => new ArmDeployerIdentity(ArmCredential(environmentVariable));

    // The principal the run authenticates as; null, with the reason on stderr, when the credential is missing, malformed or refused.
    internal static async Task<DeployerIdentity?> GetIdentityAsync(
        IDeployerIdentity? identity, Func<string, string?> environmentVariable, TextWriter stderr, CancellationToken cancellationToken)
    {
        try
        {
            return await (identity ?? CreateDeployerIdentity(environmentVariable)).GetAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return null;
        }
    }

    // The live revisions are read with the same credential as the identity, so the principal named in a record is the one that observed.
    internal static ILiveRevisions CreateLiveRevisions(Func<string, string?> environmentVariable) => new ArmLiveRevisions(ArmCredential(environmentVariable));

    private static TokenCredential ArmCredential(Func<string, string?> environmentVariable) =>
        new ArmCredentials(environmentVariable(ArmCredentials.TenantVariable), environmentVariable(ArmCredentials.ClientVariable), environmentVariable(ArmCredentials.SecretVariable)).ToCredential();

    internal static IBackEndProvider CreatePulumiBackend(Func<string, string?> environmentVariable)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterPulumiBackend(PulumiSettings(environmentVariable));
        return services.BuildServiceProvider().GetRequiredService<IBackEndProvider>();
    }

    // The Pulumi backend on the state in jd's data directory (created on first use) and its key, with the optional Pulumi home and scratch directory.
    internal static Action<PulumiBackendOptions> PulumiSettings(Func<string, string?> environmentVariable)
    {
        var (state, key) = DataDirectory.Resolve(environmentVariable).PrepareState();
        return options =>
        {
            options.BackendUrl = "file://" + state;
            options.ConfigPassPhraseFile = key;
            options.PulumiHome = environmentVariable("PULUMI_HOME");
            options.ScratchDirectory = environmentVariable("JD_SCRATCH_DIR") ?? options.ScratchDirectory;
        };
    }

    internal static void Print(IEnumerable<LoadError> errors, TextWriter writer)
    {
        foreach (var error in errors)
        {
            writer.WriteLine(error);
        }
    }

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        var command = args.FirstOrDefault();
        if (command is not ("validate" or "preview" or "deploy"))
        {
            return (null, command is null ? "no command given." : $"unknown command '{command}'.");
        }

        var (scanned, problem) = Arguments.Scan(args, 1);
        if (scanned is null)
        {
            return (null, problem);
        }

        var (positional, environment, catalog, json, dryRun) = scanned;
        if (positional.Count != 1)
        {
            return (null, "expected exactly one workload file.");
        }

        if (command == "validate")
        {
            return environment is null && catalog is null && !json && !dryRun
                ? (new ValidateOptions(positional[0]), string.Empty)
                : (null, "validate takes only a workload file.");
        }

        if (environment is null || catalog is null)
        {
            return (null, $"{command} needs --env and --catalog.");
        }

        if (command == "deploy")
        {
            return json ? (null, "deploy has no --json option.") : (new DeployOptions(positional[0], environment, catalog, dryRun), string.Empty);
        }

        return dryRun ? (null, "preview has no --preview option.") : (new PreviewOptions(positional[0], environment, catalog, json), string.Empty);
    }
}
