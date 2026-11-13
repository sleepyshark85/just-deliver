using System.Globalization;
using System.Text;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.release;

namespace jd.cli;

/// <summary>
/// The <c>jd release</c> commands: <c>create</c> and <c>show</c> read their files, hand them to <see cref="CreateRelease"/> and
/// <see cref="ShowRelease"/>, print the result and write the set to <c>--out</c>; <c>deploy</c> and <c>qualify</c> go to <see cref="ReleaseDeployCli"/>.
/// Parsing, reading, writing and output only; exit codes from <see cref="ExitCodes"/>. A read or write that fails is the dispatch's to report.
/// </summary>
internal static class ReleaseCli
{
    private const string CreateCommand = "create";
    private const string ShowCommand = "show";
    private const string DeployCommand = "deploy";
    private const string QualifyCommand = "qualify";

    // Out is set exactly for create.
    private sealed record Options(string Input, string? Out);

    // args are the arguments after "release".
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, Connections connections, CancellationToken cancellationToken)
    {
        if (args.FirstOrDefault() is DeployCommand or QualifyCommand)
        {
            return await ReleaseDeployCli.RunAsync(args[1..], args[0] == QualifyCommand, stdout, stderr, connections, cancellationToken);
        }

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

        return options.Out is { } output
            ? await CreateAsync(options.Input, output, connections, stdout, stderr, cancellationToken)
            : await ShowAsync(options.Input, stdout, stderr, cancellationToken);
    }

    private static async Task<int> CreateAsync(
        string manifest, string output, Connections connections, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var input = new CreateReleaseInput(await Cli.ReadDocumentAsync(manifest, cancellationToken), BesideManifest(manifest));
        var result = await CreateRelease.RunAsync(input, connections, cancellationToken);
        Cli.Print(result.Errors, stderr);
        if (result is { Set: { } set, Document: { } document })
        {
            // CreateNew: an --out that appeared since the check is still never overwritten.
            await using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            {
                await stream.WriteAsync(document, cancellationToken);
            }

            stdout.WriteLine($"{output}: release {set.Label} with {set.Workloads.Count} workload(s), deploy order {string.Join(", ", set.Order)}.");
        }

        return ExitCodes.Of(result.Outcome);
    }

    private static async Task<int> ShowAsync(string path, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var result = await ShowRelease.RunAsync(new ShowReleaseInput(await Cli.ReadDocumentAsync(path, cancellationToken)), cancellationToken);
        Cli.Print(result.Errors, stderr);
        if (result is { Set: { } set, CreatedAt: { } createdAt })
        {
            stdout.Write(Format(set, createdAt));
        }

        return ExitCodes.Of(result.Outcome);
    }

    // The definitions a manifest names are files beside it: each reference is resolved against the manifest's directory, and that path names it.
    private static DocumentReader BesideManifest(string manifest)
    {
        // The manifest exists, so its full path has a directory.
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        return async (reference, cancellationToken) =>
        {
            var path = Path.Combine(directory, reference);
            if (!File.Exists(path))
            {
                return (path, null);
            }

            return (path, await File.ReadAllBytesAsync(path, cancellationToken));
        };
    }

    // The input must exist; an existing --out is refused up front, before anything is read, because a release set is never changed.
    private static string? Unusable(Options options) =>
        Arguments.Unreadable(options.Input, null, null)
        ?? (options.Out is { } output && File.Exists(output) ? $"'{output}' already exists; a release set is immutable, so create a new file." : null);

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        var command = args.FirstOrDefault();
        if (command is not (CreateCommand or ShowCommand))
        {
            return (null, command is null
                ? $"{Cli.ReleaseCommand} needs a command: {CreateCommand}, {ShowCommand}, {DeployCommand} or {QualifyCommand}."
                : $"unknown {Cli.ReleaseCommand} command '{command}'.");
        }

        var (scanned, problem) = Arguments.ScanOptions(args[1..], command == CreateCommand ? [Arguments.OutOption] : [], []);
        if (scanned is null)
        {
            return (null, problem);
        }

        var (positional, values, _) = scanned;
        if (positional.Count != 1)
        {
            return (null, command == CreateCommand ? "expected exactly one release manifest." : "expected exactly one release set.");
        }

        var output = values.GetValueOrDefault(Arguments.OutOption);
        return command == CreateCommand && output is null
            ? (null, $"{CreateCommand} needs {Arguments.OutOption} <release-set.yaml>.")
            : (new Options(positional[0], output), string.Empty);
    }

    private static string Format(ReleaseSet set, string createdAt)
    {
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"release {set.Label} (created {createdAt})")
            .AppendLine("deploy order:");
        for (var i = 0; i < set.Order.Count; i++)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {i + 1}. {set.Order[i]}");
        }

        text.AppendLine("workloads:");
        foreach (var workload in set.Workloads)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {workload.Name}")
                .AppendLine(CultureInfo.InvariantCulture, $"    team:       {workload.Team}")
                .AppendLine(CultureInfo.InvariantCulture, $"    sha256:     {workload.DefinitionSha256}")
                .AppendLine(CultureInfo.InvariantCulture, $"    image:      {workload.Image}")
                .AppendLine(CultureInfo.InvariantCulture, $"    depends on: {(workload.DependsOn.Count == 0 ? "(none)" : string.Join(", ", workload.DependsOn))}");
        }

        return text.ToString();
    }
}
