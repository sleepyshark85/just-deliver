using jd.definitionvalidator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.graph;
using jd.resolver.policies;
using Newtonsoft.Json.Linq;

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

    private const string Usage = """
        Usage:
          jd validate <workload.yaml>
          jd preview <workload.yaml> --env <environment.yaml> --catalog <dir> [--json]
          jd --help

        validate  checks a workload against its schema and rules.
        preview   validates the workload, then prints the resolved graph (--json: as stable JSON).
        Exit codes: 0 success, 1 validation or resolution errors, 2 usage errors.

        """;

    private sealed record Options(string Command, string? Workload, string? Environment, string? Catalog, bool Json);

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            stdout.Write(Usage);
            return Success;
        }

        var (options, problem) = Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Usage);
            return UsageError;
        }

        var missing = new[] { options.Workload, options.Environment, options.Catalog }.Where(p => p is not null).FirstOrDefault(p => !File.Exists(p) && !Directory.Exists(p));
        if (missing is not null)
        {
            stderr.WriteLine($"jd: cannot read '{missing}': not found.");
            return UsageError;
        }

        var (workload, workloadErrors) = await LoadWorkloadAsync(options.Workload!, cancellationToken);
        if (workload is null)
        {
            workloadErrors.ForEach(stderr.WriteLine);
            return Invalid;
        }

        if (options.Command == "validate")
        {
            stdout.WriteLine($"{options.Workload}: valid.");
            return Success;
        }

        var catalogResult = await CatalogDirectory.LoadAsync(options.Catalog!, cancellationToken);
        var environmentResult = await EnvironmentFile.LoadAsync(options.Environment!, cancellationToken);
        if (catalogResult.Catalog is not { } catalog || environmentResult.Descriptor is not { } environment)
        {
            catalogResult.Errors.Concat(environmentResult.Errors).ToList().ForEach(e => stderr.WriteLine(e));
            return Invalid;
        }

        var graph = new GraphBuilder(environment).Build(new PolicyApplier(catalog, environment).Apply(new Expander(catalog, environment).Expand(workload, options.Workload!)));
        if (graph.Errors.Count > 0)
        {
            graph.Errors.ToList().ForEach(e => stderr.WriteLine(e));
            return Invalid;
        }

        stdout.Write(options.Json ? GraphJson.Serialize(graph) : PreviewFormatter.Format(graph));
        return Success;
    }

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        string? command = args.FirstOrDefault();
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
                    if (++i == args.Length)
                    {
                        return (null, $"{args[i - 1]} needs a value.");
                    }

                    if (args[i - 1] == "--env")
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

        if (command == "preview" && (environment is null || catalog is null))
        {
            return (null, "preview needs --env and --catalog.");
        }

        if (command == "validate" && (environment is not null || catalog is not null || json))
        {
            return (null, "validate takes only a workload file.");
        }

        return (new Options(command, positional[0], environment, catalog, json), string.Empty);
    }

    // The same two steps for both commands: the resolver's graph relies on a workload that passed the schema and rules.
    private static async Task<(JObject? Workload, List<string> Errors)> LoadWorkloadAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        JToken document;
        try
        {
            document = YamlSchemaValidator.ParseYaml(text);
        }
        catch (Exception ex)
        {
            return (null, [$"{path}: {YamlSchemaValidator.DescribeYamlError(ex, text)}"]);
        }

        var result = await new YamlSchemaValidator(WorkloadRules.Check).ValidateTokenAsync(await WorkloadSchema.ReadAsync(cancellationToken), document);
        return result.IsValid && document is JObject workload ? (workload, []) : (null, result.Errors.Select(e => $"{path}: {e}").ToList());
    }
}
