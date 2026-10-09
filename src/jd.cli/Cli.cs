using jd.bp.pulumi;
using jd.core.bp;
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
          jd deploy <workload.yaml> --env <environment.yaml> --catalog <dir> [--preview]
          jd --help

        validate  checks a workload against its schema and rules.
        preview   validates the workload, then prints the resolved graph (--json: as stable JSON).
        release   create writes an immutable release set (pinned workloads, deploy order) from a manifest; show prints one.
        deploy    provisions the infrastructure nodes in order (--preview: only shows the changes; nothing is created).
                  Backend: PULUMI_BACKEND_URL and PULUMI_CONFIG_PASSPHRASE (or _FILE) are required; PULUMI_HOME, JD_SCRATCH_DIR optional.
        Exit codes: 0 success, 1 validation, resolution or deployment errors, 2 usage errors.

        """;

    private abstract record Options(string Workload);

    private sealed record ValidateOptions(string Workload) : Options(Workload);

    private abstract record ResolveOptions(string Workload, string Environment, string Catalog) : Options(Workload);

    private sealed record PreviewOptions(string Workload, string Environment, string Catalog, bool Json) : ResolveOptions(Workload, Environment, Catalog);

    private sealed record DeployOptions(string Workload, string Environment, string Catalog, bool DryRun) : ResolveOptions(Workload, Environment, Catalog);

    private const string BackendUrlVariable = "PULUMI_BACKEND_URL";
    private const string PassphraseVariable = "PULUMI_CONFIG_PASSPHRASE";
    private const string PassphraseFileVariable = "PULUMI_CONFIG_PASSPHRASE_FILE";

    // backend is the one deploy uses; when null, the Pulumi backend configured from the process environment variables.
    // environmentVariable reads them (null: the process environment). Both are test seams.
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default, IBackEndProvider? backend = null, Func<string, string?>? environmentVariable = null)
    {
        environmentVariable ??= System.Environment.GetEnvironmentVariable;
        if (args.Contains("--help") || args.Contains("-h"))
        {
            stdout.Write(Usage);
            return Success;
        }

        if (args.FirstOrDefault() == "release")
        {
            return await ReleaseCli.RunAsync(args[1..], stdout, stderr, cancellationToken);
        }

        var (options, problem) = Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Usage);
            return UsageError;
        }

        var unreadable = Unreadable(options);
        if (unreadable is not null)
        {
            stderr.WriteLine($"jd: cannot read '{unreadable.Value.Path}': {unreadable.Value.Reason}");
            return UsageError;
        }

        if (options is DeployOptions && backend is null && BackendSettingsProblem(environmentVariable) is { } missing)
        {
            stderr.WriteLine($"jd: {missing}");
            return UsageError;
        }

        try
        {
            return await RunCommandAsync(options, backend, environmentVariable, stdout, stderr, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The inputs exist (checked above), so this is a permission or read failure; .NET's message names the file.
            stderr.WriteLine($"jd: {ex.Message}");
            return UsageError;
        }
    }

    private static async Task<int> RunCommandAsync(Options options, IBackEndProvider? backend, Func<string, string?> environmentVariable, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var loaded = await WorkloadFile.LoadAsync(options.Workload, cancellationToken);
        if (loaded.Workload is not { } workload)
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
        var environmentResult = await EnvironmentFile.LoadAsync(resolve.Environment, cancellationToken);
        if (catalogResult.Catalog is not { } catalog || environmentResult.Descriptor is not { } environment)
        {
            // Catalog errors name files relative to the catalog root; the person needs the path they can open.
            // An error about the catalog as a whole names no real file and is left as it is.
            Print(catalogResult.Errors.Select(e => Path.Combine(resolve.Catalog, e.File) is var full && File.Exists(full) ? e with { File = full } : e)
                .Concat(environmentResult.Errors), stderr);
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
            return await DeployAsync(deploy, graph, catalog, environment, backend, environmentVariable, stdout, stderr, cancellationToken);
        }

        stdout.Write(resolve is PreviewOptions { Json: true } ? GraphJson.Serialize(graph) : PreviewFormatter.Format(graph));
        return Success;
    }

    // Templates live in the catalog's templates directory; the graph must fit them before anything is created.
    private static async Task<int> DeployAsync(
        DeployOptions options, ResolvedGraph graph, Catalog catalog, EnvironmentDescriptor environment, IBackEndProvider? backend,
        Func<string, string?> environmentVariable, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var templates = await TemplateLibrary.LoadAsync(Path.Combine(options.Catalog, CatalogDirectory.TemplatesDirectory), cancellationToken);
        var misfits = templates.Check(graph);
        if (misfits.Count > 0)
        {
            Print(misfits, stderr);
            return Invalid;
        }

        var orchestrator = new Orchestrator(backend ?? CreatePulumiBackend(environmentVariable), templates, catalog, environment);
        void Show(NodeReport report) => stdout.Write(DeployFormatter.Format(report));
        var run = options.DryRun
            ? await orchestrator.PreviewAsync(graph, Show, cancellationToken)
            : await orchestrator.DeployAsync(graph, Show, cancellationToken);
        if (!run.Succeeded)
        {
            stderr.WriteLine($"jd: stopped at {run.Nodes[^1].NodeId}: {run.Nodes[^1].Message}");
            return Invalid;
        }

        return Success;
    }

    // State location and secrets passphrase must be set explicitly: a default would lose track of stacks between runs, or
    // encrypt with a passphrase nobody chose. An empty passphrase is a choice (local/dev); a passphrase file is another.
    private static string? BackendSettingsProblem(Func<string, string?> environmentVariable)
    {
        if (environmentVariable(BackendUrlVariable) is null)
        {
            return $"{BackendUrlVariable} is not set. Set it to where Pulumi keeps state, for example file://<directory> or an Azure blob URL; without it stacks would be lost between runs.";
        }

        return environmentVariable(PassphraseVariable) is null && environmentVariable(PassphraseFileVariable) is null
            ? $"{PassphraseVariable} is not set. Set it (empty is acceptable for local/dev only), or set {PassphraseFileVariable}."
            : null;
    }

    private static IBackEndProvider CreatePulumiBackend(Func<string, string?> environmentVariable)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterPulumiBackend(options =>
        {
            options.BackendUrl = environmentVariable(BackendUrlVariable);
            options.ConfigPassPhrase = environmentVariable(PassphraseVariable);
            options.PulumiHome = environmentVariable("PULUMI_HOME");
            options.ScratchDirectory = environmentVariable("JD_SCRATCH_DIR") ?? options.ScratchDirectory;
        });
        return services.BuildServiceProvider().GetRequiredService<IBackEndProvider>();
    }

    private static void Print(IEnumerable<LoadError> errors, TextWriter writer)
    {
        foreach (var error in errors)
        {
            writer.WriteLine(error);
        }
    }

    // Each input must be what its role needs: the workload and environment are files, the catalog is a directory.
    private static (string Path, string Reason)? Unreadable(Options options)
    {
        if (!File.Exists(options.Workload))
        {
            return (options.Workload, "not found or not a file.");
        }

        if (options is not ResolveOptions resolve)
        {
            return null;
        }

        if (!File.Exists(resolve.Environment))
        {
            return (resolve.Environment, "not found or not a file.");
        }

        return Directory.Exists(resolve.Catalog) ? null : (resolve.Catalog, "not found or not a directory.");
    }

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        var command = args.FirstOrDefault();
        if (command is not ("validate" or "preview" or "deploy"))
        {
            return (null, command is null ? "no command given." : $"unknown command '{command}'.");
        }

        string? environment = null, catalog = null;
        bool json = false, dryRun = false;
        var positional = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--preview":
                    dryRun = true;
                    break;
                case "--env" or "--catalog":
                    if (i + 1 == args.Length)
                    {
                        return (null, $"{args[i]} needs a value.");
                    }

                    if (args[i++] == "--env")
                    {
                        environment = args[i];
                    }
                    else
                    {
                        catalog = args[i];
                    }

                    break;
                case var option when option.StartsWith("--", StringComparison.Ordinal):
                    return (null, $"unknown option '{option}'.");
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

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
