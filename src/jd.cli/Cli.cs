using jd.orchestrator;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.graph;

namespace jd.cli;

/// <summary>
/// The command line: hands each command to its own command type, or <c>preview</c> and <c>deploy</c> to their use cases over the
/// <see cref="Connections"/> of <see cref="Composition"/>, and returns the <see cref="ExitCodes"/>. Parsing, reading, formatting; no rules.
/// </summary>
internal static class Cli
{
    internal const string ValidateCommand = "validate";
    private const string PreviewCommand = "preview";
    private const string DeployCommand = "deploy";
    internal const string ReleaseCommand = "release";
    internal const string ApproveCommand = "approve";
    internal const string PromoteCommand = "promote";
    internal const string EnvCommand = "env";
    private const string HelpOption = "--help";
    private const string ShortHelpOption = "-h";

    // The problem when a workload command is not given exactly one workload file.
    internal const string OneWorkloadProblem = "expected exactly one workload file.";

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

    private abstract record ResolveOptions(string Workload, string Environment, string Catalog);

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
        if (args.Contains(HelpOption) || args.Contains(ShortHelpOption))
        {
            stdout.Write(Usage);
            return ExitCodes.Success;
        }

        // A command that reads its inputs checks first that they exist, so an I/O failure that escapes one is a permission, read or write failure (a data
        // directory that cannot be written, a file gone since); .NET's message names the file.
        try
        {
            return await DispatchAsync(args, stdout, stderr, connections, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return ExitCodes.UsageError;
        }
    }

    private static async Task<int> DispatchAsync(string[] args, TextWriter stdout, TextWriter stderr, Connections connections, CancellationToken cancellationToken) =>
        args.FirstOrDefault() switch
        {
            ReleaseCommand => await ReleaseCli.RunAsync(args[1..], stdout, stderr, connections, cancellationToken),
            ApproveCommand => await ApproveCli.RunAsync(args[1..], stdout, stderr, connections, cancellationToken),
            PromoteCommand => await PromoteCli.RunAsync(args[1..], stdout, stderr, connections, cancellationToken),
            EnvCommand => await EnvCli.RunAsync(args[1..], stdout, stderr, connections, cancellationToken),
            ValidateCommand => await ValidateCli.RunAsync(args[1..], stdout, stderr, cancellationToken),
            PreviewCommand or DeployCommand => await RunWorkloadAsync(args, connections, stdout, stderr, cancellationToken),
            null => UsageProblem("no command given.", stderr),
            var unknown => UsageProblem($"unknown command '{unknown}'.", stderr),
        };

    // A command line jd cannot use: the problem, then the usage.
    private static int UsageProblem(string problem, TextWriter stderr)
    {
        stderr.WriteLine($"jd: {problem}");
        stderr.Write(Usage);
        return ExitCodes.UsageError;
    }

    // preview and deploy: their arguments, then their inputs read as typed, then the use case.
    private static async Task<int> RunWorkloadAsync(string[] args, Connections connections, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var (options, problem) = Parse(args);
        if (options is null)
        {
            return UsageProblem(problem, stderr);
        }

        if (Arguments.Unreadable(options.Workload, options.Environment, options.Catalog) is { } unreadable)
        {
            stderr.WriteLine($"jd: {unreadable}");
            return ExitCodes.UsageError;
        }

        var workload = await ReadDocumentAsync(options.Workload, cancellationToken);
        var environment = await ReadDocumentAsync(options.Environment, cancellationToken);
        var catalog = await ReadCatalogAsync(options.Catalog, cancellationToken);
        return options switch
        {
            DeployOptions deploy => await DeployAsync(new DeployInput(workload, environment, catalog, deploy.DryRun), connections, stdout, stderr, cancellationToken),
            PreviewOptions preview => await PreviewAsync(
                new PreviewInput(workload, environment, catalog), preview.Json ? GraphJson.Serialize : PreviewFormatter.Format, stdout, stderr, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(args), options, "not a preview or deploy."),
        };
    }

    private static async Task<int> PreviewAsync(
        PreviewInput input, Func<ResolvedGraph, string> format, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var result = await Preview.RunAsync(input, cancellationToken);
        Print(result.Errors, stderr);
        if (result.Graph is { } graph)
        {
            stdout.Write(format(graph));
        }

        return ExitCodes.Of(result.Outcome);
    }

    private static async Task<int> DeployAsync(
        DeployInput input, Connections connections, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var result = await Deploy.RunAsync(input, connections, ShowWalk(stdout), cancellationToken);
        Print(result.Errors, stderr);
        if (result.Problem is { } problem)
        {
            stderr.WriteLine(Refusal(problem, DeployCommand));
        }

        if (result.Run is { Succeeded: false } run)
        {
            PrintStopped(run, stderr);
        }

        return ExitCodes.Of(result.Outcome);
    }

    // The walk's lines as each node finishes; jd deploy prints no line when a workload starts (jd env up has no workloads).
    internal static Action<Progress> ShowWalk(TextWriter stdout) => progress =>
    {
        if (progress is NodeFinished finished)
        {
            stdout.Write(DeployFormatter.Format(finished.Report));
        }
    };

    // The words of a refusal, around the problem's values.
    internal static string Refusal(Problem problem, string command) => problem switch
    {
        Gated gated => $"jd: {command}: environment '{gated.Environment}' is gated by an approval policy: use `jd promote`.",
        KeptSetChanged changed => $"jd: {command}: the kept set '{changed.Set}' changed since it was checked (expected sha256 {changed.ExpectedSha256}).",
        IdentityRefused refused => $"jd: {refused.Reason}",
        RecordingRefused refused => $"jd: {refused.Reason}",
        ConnectionFailed failed => $"jd: {failed.Reason}",
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "not a refusal a deploy meets."),
    };

    // The failed node of a walk that stopped, and why.
    internal static void PrintStopped(RunReport run, TextWriter stderr) => stderr.WriteLine($"jd: stopped at {run.Nodes[^1].NodeId}: {run.Nodes[^1].Message}");

    internal static async Task<SourceDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken) =>
        new(path, await File.ReadAllBytesAsync(path, cancellationToken));

    // The catalog argument, which exists, as typed, and every *.yaml under it named relative to it as the catalog layout names it.
    internal static async Task<SourceCatalog> ReadCatalogAsync(string directory, CancellationToken cancellationToken)
    {
        var documents = new List<SourceDocument>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories))
        {
            documents.Add(new SourceDocument(Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/'), await File.ReadAllBytesAsync(file, cancellationToken)));
        }

        return new SourceCatalog(directory, documents);
    }

    internal static void Print(IEnumerable<LoadError> errors, TextWriter writer)
    {
        foreach (var error in errors)
        {
            writer.WriteLine(error);
        }
    }

    // args start with the command, preview or deploy.
    private static (ResolveOptions? Options, string Problem) Parse(string[] args)
    {
        var command = args[0];
        var (scanned, problem) = Arguments.Scan(args[1..]);
        if (scanned is null)
        {
            return (null, problem);
        }

        var (positional, environment, catalog, json, dryRun) = scanned;
        if (positional.Count != 1)
        {
            return (null, OneWorkloadProblem);
        }

        if (environment is null || catalog is null)
        {
            return (null, $"{command} needs {Arguments.EnvOption} and {Arguments.CatalogOption}.");
        }

        if (command == DeployCommand)
        {
            return json ? (null, $"{command} has no {Arguments.JsonOption} option.") : (new DeployOptions(positional[0], environment, catalog, dryRun), string.Empty);
        }

        return dryRun ? (null, $"{command} has no {Arguments.PreviewOption} option.") : (new PreviewOptions(positional[0], environment, catalog, json), string.Empty);
    }
}
