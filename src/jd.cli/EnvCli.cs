using jd.core.bp;
using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;

namespace jd.cli;

/// <summary>
/// <c>jd env up</c>: resolves an environment definition through the resolver, provisions it through the orchestrator and writes the
/// descriptor from the outputs. Wiring only; the rules are <see cref="DescriptorComposer"/>'s. Exit codes as <see cref="Cli"/>.
/// </summary>
internal static class EnvCli
{
    private const int Success = 0;
    private const int Invalid = 1;
    private const int UsageError = 2;
    private const string RegionVariable = "JD_REGION";

    private sealed record Options(string Definition, string Catalog, string Out, string? Base, string? Region, bool Force);

    // args are the arguments after "env".
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

        var region = options.Region ?? environmentVariable(RegionVariable);
        problem = region is null ? $"no region: pass --region or set {RegionVariable}." : Unreadable(options) ?? (backend is null ? Cli.BackendSettingsProblem(environmentVariable) : null);
        if (problem is not null)
        {
            stderr.WriteLine($"jd: {problem}");
            return UsageError;
        }

        try
        {
            return await UpAsync(options, region!, backend, environmentVariable, stdout, stderr, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return UsageError;
        }
    }

    private static async Task<int> UpAsync(
        Options options, string region, IBackEndProvider? backend, Func<string, string?> environmentVariable, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var definitionResult = await EnvironmentDefinitionFile.LoadAsync(options.Definition, cancellationToken);
        var catalogResult = await CatalogDirectory.LoadAsync(options.Catalog, cancellationToken);
        var baseResult = options.Base is null ? null : await EnvironmentFile.LoadAsync(options.Base, cancellationToken);
        if (definitionResult.Definition is not { } definition || catalogResult.Catalog is not { } catalog || (baseResult is not null && baseResult.Descriptor is null))
        {
            Cli.Print(definitionResult.Errors.Concat(Cli.CatalogErrors(catalogResult, options.Catalog)).Concat(baseResult?.Errors ?? []), stderr);
            return Invalid;
        }

        var environment = definition.Over(region, baseResult?.Descriptor);
        var graph = Resolver.ResolveSubstrate(definition, options.Definition, catalog, environment);
        var composer = new DescriptorComposer(definition, options.Definition, catalog, environment, graph);

        // Everything that can be known before anything is created is checked first: the graph, and the descriptor with its values still pending.
        var dryRun = graph.Errors.Count > 0 ? new ComposedDescriptor(null, graph.Errors) : await composer.ComposeAsync(null, cancellationToken);
        if (dryRun.Errors.Count > 0)
        {
            Cli.Print(dryRun.Errors, stderr);
            return Invalid;
        }

        var (code, run) = await Cli.ProvisionAsync(options.Catalog, graph, catalog, environment, backend, environmentVariable, dryRun: false, stdout, stderr, cancellationToken);
        if (run is null || code != Success)
        {
            return code;
        }

        // Secret or null outputs are left out, so a value that needs one cannot be filled and is reported.
        var outputs = run.Nodes.ToDictionary(
            n => n.NodeId,
            n => (IReadOnlyDictionary<string, string>)n.Outputs.Where(o => o.Value is { IsSecret: false, Value: not null }).ToDictionary(o => o.Key, o => o.Value.Value!));
        var composed = await composer.ComposeAsync(outputs, cancellationToken);
        if (composed.Yaml is not { } yaml)
        {
            Cli.Print(composed.Errors, stderr);
            return Invalid;
        }

        await using (var stream = new FileStream(options.Out, options.Force ? FileMode.Create : FileMode.CreateNew, FileAccess.Write))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(yaml.AsMemory(), cancellationToken);
        }

        stdout.WriteLine($"{options.Out}: environment {environment.Name} ({environment.Region}, {environment.Tier}) written.");
        return Success;
    }

    // Each input must be what its role needs; an existing --out is refused up front, before anything is created.
    private static string? Unreadable(Options options)
    {
        if (!File.Exists(options.Definition))
        {
            return $"cannot read '{options.Definition}': not found or not a file.";
        }

        if (options.Base is not null && !File.Exists(options.Base))
        {
            return $"cannot read '{options.Base}': not found or not a file.";
        }

        if (!Directory.Exists(options.Catalog))
        {
            return $"cannot read '{options.Catalog}': not found or not a directory.";
        }

        return File.Exists(options.Out) && !options.Force ? $"'{options.Out}' already exists; pass --force to overwrite it." : null;
    }

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        if (args.FirstOrDefault() != "up")
        {
            return (null, args.Length == 0 ? "env needs a command: up." : $"unknown env command '{args[0]}'.");
        }

        var values = new Dictionary<string, string>();
        var force = false;
        var positional = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--force":
                    force = true;
                    break;
                case "--catalog" or "--out" or "--base" or "--region":
                    if (i + 1 == args.Length)
                    {
                        return (null, $"{args[i]} needs a value.");
                    }

                    values[args[i]] = args[i + 1];
                    i++;
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
            return (null, "expected exactly one definition file.");
        }

        return values.TryGetValue("--catalog", out var catalog) && values.TryGetValue("--out", out var output)
            ? (new Options(positional[0], catalog, output, values.GetValueOrDefault("--base"), values.GetValueOrDefault("--region"), force), string.Empty)
            : (null, "env up needs --catalog and --out.");
    }
}
