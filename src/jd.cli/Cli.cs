using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.graph;
using jd.resolver.policies;
using jd.resolver.workload;

namespace jd.cli;

/// <summary>
/// The composition root: parses arguments, wires the validator and the resolver stages, prints results and returns the exit code
/// (0 success, 1 validation or resolution errors, 2 usage errors). Holds no resolution logic.
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
          jd --help

        validate  checks a workload against its schema and rules.
        preview   validates the workload, then prints the resolved graph (--json: as stable JSON).
        release   create writes an immutable release set (pinned workloads, deploy order) from a manifest; show prints one.
        Exit codes: 0 success, 1 validation or resolution errors, 2 usage errors.

        """;

    private abstract record Options(string Workload);

    private sealed record ValidateOptions(string Workload) : Options(Workload);

    private sealed record PreviewOptions(string Workload, string Environment, string Catalog, bool Json) : Options(Workload);

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
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

        try
        {
            return await RunCommandAsync(options, stdout, stderr, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The inputs exist (checked above), so this is a permission or read failure; .NET's message names the file.
            stderr.WriteLine($"jd: {ex.Message}");
            return UsageError;
        }
    }

    private static async Task<int> RunCommandAsync(Options options, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var loaded = await WorkloadFile.LoadAsync(options.Workload, cancellationToken);
        if (loaded.Workload is not { } workload)
        {
            Print(loaded.Errors, stderr);
            return Invalid;
        }

        if (options is not PreviewOptions preview)
        {
            stdout.WriteLine($"{options.Workload}: valid.");
            return Success;
        }

        var catalogResult = await CatalogDirectory.LoadAsync(preview.Catalog, cancellationToken);
        var environmentResult = await EnvironmentFile.LoadAsync(preview.Environment, cancellationToken);
        if (catalogResult.Catalog is not { } catalog || environmentResult.Descriptor is not { } environment)
        {
            // Catalog errors name files relative to the catalog root; the person needs the path they can open.
            // An error about the catalog as a whole names no real file and is left as it is.
            Print(catalogResult.Errors.Select(e => Path.Combine(preview.Catalog, e.File) is var full && File.Exists(full) ? e with { File = full } : e)
                .Concat(environmentResult.Errors), stderr);
            return Invalid;
        }

        var graph = new GraphBuilder(environment).Build(new PolicyApplier(catalog, environment).Apply(new Expander(catalog, environment).Expand(workload, options.Workload)));
        if (graph.Errors.Count > 0)
        {
            Print(graph.Errors, stderr);
            return Invalid;
        }

        stdout.Write(preview.Json ? GraphJson.Serialize(graph) : PreviewFormatter.Format(graph));
        return Success;
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

        if (options is not PreviewOptions preview)
        {
            return null;
        }

        if (!File.Exists(preview.Environment))
        {
            return (preview.Environment, "not found or not a file.");
        }

        return Directory.Exists(preview.Catalog) ? null : (preview.Catalog, "not found or not a directory.");
    }

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        var command = args.FirstOrDefault();
        if (command is not ("validate" or "preview"))
        {
            return (null, command is null ? "no command given." : $"unknown command '{command}'.");
        }

        string? environment = null, catalog = null;
        var json = false;
        var positional = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
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
            return environment is null && catalog is null && !json
                ? (new ValidateOptions(positional[0]), string.Empty)
                : (null, "validate takes only a workload file.");
        }

        return environment is null || catalog is null
            ? (null, "preview needs --env and --catalog.")
            : (new PreviewOptions(positional[0], environment, catalog, json), string.Empty);
    }
}
