using jd.orchestrator.usecases;

namespace jd.cli;

/// <summary>
/// <c>jd promote</c>: reads the target's descriptor and the catalog named on the command line, hands them and the label to <see cref="Promote"/>, and
/// prints the walk as it happens, then what it did or why it refused (docs/architecture/approvals.md section 5). Parsing, reading, formatting; no rules.
/// </summary>
internal static class PromoteCli
{
    private const string ToOption = "--to";

    private sealed record Options(string Label, string Target, string Catalog);

    // args are the arguments after "promote".
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, Connections connections, CancellationToken cancellationToken)
    {
        var (options, problem) = Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Cli.Usage);
            return ExitCodes.UsageError;
        }

        if (Arguments.Unreadable(options.Target, null, options.Catalog) is { } unreadable)
        {
            stderr.WriteLine($"jd: {unreadable}");
            return ExitCodes.UsageError;
        }

        var input = new PromoteInput(
            options.Label, await Cli.ReadDocumentAsync(options.Target, cancellationToken), await Cli.ReadCatalogAsync(options.Catalog, cancellationToken));
        var result = await Promote.RunAsync(input, connections, Show(stdout, stderr), cancellationToken);
        if (result.Release is { } release)
        {
            return ReleaseDeployCli.PrintDeploy(release, Cli.PromoteCommand, stdout, stderr);
        }

        Cli.Print(result.Errors, stderr);
        if (result.Problem is { } refusal)
        {
            stderr.WriteLine(Refusal(refusal));
        }

        if (result.Outcome is Outcome.InvalidInput)
        {
            stderr.Write(Cli.Usage);
        }

        return ExitCodes.Of(result.Outcome);
    }

    // The files skipped on stderr, and the start and the lines of the walk on stdout, as they happen.
    private static Action<Progress> Show(TextWriter stdout, TextWriter stderr)
    {
        var walk = ReleaseDeployCli.ShowWalk(stdout);
        return progress =>
        {
            switch (progress)
            {
                case Ignored ignored:
                    stderr.WriteLine($"jd: {Cli.PromoteCommand}: ignored {ignored.What}");
                    break;
                case Promoting promoting:
                    stdout.WriteLine($"promoting {promoting.Label} from {promoting.From} (set sha256 {promoting.SetSha256})");
                    break;
                default:
                    walk(progress);
                    break;
            }
        };
    }

    // The words of a refusal before the walk, around the problem's values.
    private static string Refusal(Problem problem) => problem switch
    {
        LabelInvalid invalid => $"jd: {invalid.Reason}",
        NotGated notGated => $"jd: {Cli.PromoteCommand}: environment '{notGated.Environment}' has no approval policy, so there is nothing to promote through.",
        IdentityRefused refused => $"jd: {refused.Reason}",
        NotPromotable notPromotable => string.Join(
            Environment.NewLine,
            [$"jd: {Cli.PromoteCommand}: release {notPromotable.Label} cannot be promoted to '{notPromotable.Environment}':", .. notPromotable.Reasons.Select(reason => $"  {reason}")]),
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "not a refusal a promotion meets before its walk."),
    };

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        var (label, values, problem) = Arguments.ScanOneLabel(args, ToOption, Arguments.CatalogOption);
        if (label is null)
        {
            return (null, problem);
        }

        return values.TryGetValue(ToOption, out var target) && values.TryGetValue(Arguments.CatalogOption, out var catalog)
            ? (new Options(label, target, catalog), string.Empty)
            : (null, $"{Cli.PromoteCommand} needs {ToOption} and {Arguments.CatalogOption}.");
    }
}
