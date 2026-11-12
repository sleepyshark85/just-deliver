using jd.bp.pulumi;
using jd.core.identity;
using jd.orchestrator;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.workload;

namespace jd.cli;

/// <summary>
/// The command line: parses arguments, hands each command to its use case, or to the resolver and the orchestrator over the <see cref="Connections"/>
/// of <see cref="Composition"/> for those not yet moved (docs/architecture/application.md section 10), prints results and returns the
/// <see cref="ExitCodes"/>. Holds no resolution logic.
/// </summary>
internal static class Cli
{
    private const string ValidateCommand = "validate";
    private const string PreviewCommand = "preview";
    private const string DeployCommand = "deploy";

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
    /// <param name="args">The command line, without the program name.</param>
    /// <param name="stdout">Where results are written.</param>
    /// <param name="stderr">Where problems and usage are written.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="connections">What the run reaches outside jd through; a test seam (null: <see cref="Composition.Connect"/> over the process environment).</param>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default, Connections? connections = null)
    {
        connections ??= Composition.Connect(System.Environment.GetEnvironmentVariable);
        if (args.Contains("--help") || args.Contains("-h"))
        {
            stdout.Write(Usage);
            return ExitCodes.Success;
        }

        if (args.FirstOrDefault() == "release")
        {
            return await ReleaseCli.RunAsync(args[1..], stdout, stderr, connections, cancellationToken);
        }

        if (args.FirstOrDefault() == "approve")
        {
            return await ApproveCli.RunAsync(args[1..], stdout, stderr, connections, cancellationToken);
        }

        if (args.FirstOrDefault() == "promote")
        {
            return await PromoteCli.RunAsync(args[1..], stdout, stderr, connections, cancellationToken);
        }

        if (args.FirstOrDefault() == "env")
        {
            return await EnvCli.RunAsync(args[1..], stdout, stderr, connections, cancellationToken);
        }

        var (options, problem) = Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Usage);
            return ExitCodes.UsageError;
        }

        var unreadable = Arguments.Unreadable(options.Workload, (options as ResolveOptions)?.Environment, (options as ResolveOptions)?.Catalog);
        if (unreadable is not null)
        {
            stderr.WriteLine($"jd: {unreadable}");
            return ExitCodes.UsageError;
        }

        try
        {
            return options is ResolveOptions resolve
                ? await ResolveAsync(resolve, connections, stdout, stderr, cancellationToken)
                : await ValidateAsync(options.Workload, stdout, stderr, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The inputs exist (checked above), so this is a permission or read failure; .NET's message names the file.
            stderr.WriteLine($"jd: {ex.Message}");
            return ExitCodes.UsageError;
        }
    }

    // The workload's bytes as read, named as typed, so the use case's errors print the path the person gave.
    private static async Task<int> ValidateAsync(string workload, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var input = new ValidateInput(new SourceDocument(workload, await File.ReadAllBytesAsync(workload, cancellationToken)));
        var result = await Validate.RunAsync(input, cancellationToken);
        Print(result.Errors, stderr);
        if (result.Outcome == Outcome.Succeeded)
        {
            stdout.WriteLine($"{workload}: valid.");
        }

        return ExitCodes.Of(result.Outcome);
    }

    private static async Task<int> ResolveAsync(ResolveOptions resolve, Connections connections, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var loaded = await WorkloadFile.LoadAsync(resolve.Workload, cancellationToken);
        if (loaded.Value is not { } workload)
        {
            Print(loaded.Errors, stderr);
            return ExitCodes.Failure;
        }

        var catalogResult = await CatalogDirectory.LoadAsync(resolve.Catalog, cancellationToken);
        var environmentResult = await EnvironmentParser.LoadAsync(resolve.Environment, cancellationToken);
        if (catalogResult.Value is not { } catalog || environmentResult.Value is not { } environment)
        {
            Print(CatalogErrors(catalogResult, resolve.Catalog).Concat(environmentResult.Errors), stderr);
            return ExitCodes.Failure;
        }

        if (RefuseType(catalog, environment, resolve.Environment, stderr))
        {
            return ExitCodes.Failure;
        }

        if (resolve is DeployOptions { DryRun: false } && RefuseGated(environment, DeployCommand, stderr))
        {
            return ExitCodes.Failure;
        }

        var graph = Resolver.Resolve(workload, resolve.Workload, catalog, environment);
        if (graph.Errors.Count > 0)
        {
            Print(graph.Errors, stderr);
            return ExitCodes.Failure;
        }

        if (resolve is DeployOptions deploy)
        {
            return (await ProvisionAsync(deploy.Catalog, graph, catalog, environment, connections, deploy.DryRun, stdout, stderr, cancellationToken)).Code;
        }

        stdout.Write(resolve is PreviewOptions { Json: true } ? GraphJson.Serialize(graph) : PreviewFormatter.Format(graph));
        return ExitCodes.Success;
    }

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

    internal static async Task<(int Code, RunReport? Run)> ProvisionAsync(
        string catalogDirectory, ResolvedGraph graph, Catalog catalog, EnvironmentDescriptor environment, Connections connections, bool dryRun, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (await CheckTemplatesAsync(catalogDirectory, [graph], stderr, cancellationToken) is not { } templates)
        {
            return (ExitCodes.Failure, null);
        }

        var orchestrator = new Orchestrator(connections.Backend(), templates, catalog, environment, connections.Probe());
        void Show(NodeReport report) => stdout.Write(DeployFormatter.Format(report));
        var run = dryRun
            ? await orchestrator.PreviewAsync(graph, Show, cancellationToken)
            : await orchestrator.DeployAsync(graph, Show, cancellationToken);
        if (!run.Succeeded)
        {
            stderr.WriteLine($"jd: stopped at {run.Nodes[^1].NodeId}: {run.Nodes[^1].Message}");
            return (ExitCodes.Failure, run);
        }

        return (ExitCodes.Success, run);
    }

    // The principal the run authenticates as; null, with the reason on stderr, when the credential is missing, malformed or refused.
    internal static async Task<DeployerIdentity?> GetIdentityAsync(Connections connections, TextWriter stderr, CancellationToken cancellationToken)
    {
        try
        {
            return await connections.Identity().GetAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return null;
        }
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
        if (command is not (ValidateCommand or PreviewCommand or DeployCommand))
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

        if (command == ValidateCommand)
        {
            return environment is null && catalog is null && !json && !dryRun
                ? (new ValidateOptions(positional[0]), string.Empty)
                : (null, "validate takes only a workload file.");
        }

        if (environment is null || catalog is null)
        {
            return (null, $"{command} needs --env and --catalog.");
        }

        if (command == DeployCommand)
        {
            return json ? (null, "deploy has no --json option.") : (new DeployOptions(positional[0], environment, catalog, dryRun), string.Empty);
        }

        return dryRun ? (null, "preview has no --preview option.") : (new PreviewOptions(positional[0], environment, catalog, json), string.Empty);
    }
}
