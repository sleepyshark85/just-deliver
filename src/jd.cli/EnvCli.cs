using jd.orchestrator.usecases;

namespace jd.cli;

/// <summary>
/// <c>jd env up</c>: reads the definition, the catalog and the base as typed, hands them to <see cref="EnvUp"/>, prints the walk and the result, and
/// writes the descriptor to <c>--out</c>. Parsing, reading, writing and output only; exit codes from <see cref="ExitCodes"/>. A read or write that
/// fails is the dispatch's to report.
/// </summary>
internal static class EnvCli
{
    private const string UpCommand = "up";

    private sealed record Options(string Definition, string Catalog, string Out, string? Base, bool Force);

    // args are the arguments after "env".
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, Connections connections, CancellationToken cancellationToken)
    {
        var (options, problem) = Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Cli.Usage);
            return ExitCodes.UsageError;
        }

        if (Unusable(options) is { } unusable)
        {
            stderr.WriteLine($"jd: {unusable}");
            return ExitCodes.UsageError;
        }

        return await UpAsync(options, connections, stdout, stderr, cancellationToken);
    }

    private static async Task<int> UpAsync(
        Options options, Connections connections, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var input = new EnvUpInput(
            await Cli.ReadDocumentAsync(options.Definition, cancellationToken),
            await Cli.ReadCatalogAsync(options.Catalog, cancellationToken),
            options.Base is null ? null : await Cli.ReadDocumentAsync(options.Base, cancellationToken));
        var result = await EnvUp.RunAsync(input, connections, Cli.ShowWalk(stdout), cancellationToken);
        Cli.Print(result.Errors, stderr);
        if (result.Run is { Succeeded: false } run)
        {
            Cli.PrintStopped(run, stderr);
        }

        if (result is { Descriptor: { } descriptor, Environment: { } environment })
        {
            await using (var stream = new FileStream(options.Out, options.Force ? FileMode.Create : FileMode.CreateNew, FileAccess.Write))
            {
                await stream.WriteAsync(descriptor, cancellationToken);
            }

            stdout.WriteLine($"{options.Out}: environment {environment.Name} ({environment.Region}, {environment.Type}) written.");
        }

        return ExitCodes.Of(result.Outcome);
    }

    // The inputs must exist; an existing --out is refused up front, before anything is read or created, unless --force says to overwrite it.
    private static string? Unusable(Options options) =>
        Arguments.Unreadable(options.Definition, options.Base, options.Catalog)
        ?? (File.Exists(options.Out) && !options.Force ? $"'{options.Out}' already exists; pass {Arguments.ForceOption} to overwrite it." : null);

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        if (args.FirstOrDefault() != UpCommand)
        {
            return (null, args.Length == 0 ? $"{Cli.EnvCommand} needs a command: {UpCommand}." : $"unknown {Cli.EnvCommand} command '{args[0]}'.");
        }

        var (scanned, problem) = Arguments.ScanOptions(args[1..], [Arguments.CatalogOption, Arguments.OutOption, Arguments.BaseOption], [Arguments.ForceOption]);
        if (scanned is null)
        {
            return (null, problem);
        }

        var (positional, values, flags) = scanned;
        if (positional.Count != 1)
        {
            return (null, "expected exactly one definition file.");
        }

        return values.TryGetValue(Arguments.CatalogOption, out var catalog) && values.TryGetValue(Arguments.OutOption, out var output)
            ? (new Options(positional[0], catalog, output, values.GetValueOrDefault(Arguments.BaseOption), flags.Contains(Arguments.ForceOption)), string.Empty)
            : (null, $"{Cli.EnvCommand} {UpCommand} needs {Arguments.CatalogOption} and {Arguments.OutOption}.");
    }
}
