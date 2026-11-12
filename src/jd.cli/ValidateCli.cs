using jd.orchestrator.usecases;
using jd.resolver;

namespace jd.cli;

/// <summary>
/// <c>jd validate</c>: reads the workload file's bytes, hands them to <see cref="Validate"/> named as typed, so its errors print the path the person
/// gave, and prints what it found. Wiring and output only; exit codes from <see cref="ExitCodes"/>.
/// </summary>
internal static class ValidateCli
{
    // args are the arguments after "validate".
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var (workload, problem) = Parse(args);
        if (workload is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Cli.Usage);
            return ExitCodes.UsageError;
        }

        if (Arguments.Unreadable(workload, null, null) is { } unreadable)
        {
            stderr.WriteLine($"jd: {unreadable}");
            return ExitCodes.UsageError;
        }

        var input = new ValidateInput(new SourceDocument(workload, await File.ReadAllBytesAsync(workload, cancellationToken)));
        var result = await Validate.RunAsync(input, cancellationToken);
        Cli.Print(result.Errors, stderr);
        if (result.Outcome == Outcome.Succeeded)
        {
            stdout.WriteLine($"{workload}: valid.");
        }

        return ExitCodes.Of(result.Outcome);
    }

    // The one workload file, or null with the problem: validate takes no option.
    private static (string? Workload, string Problem) Parse(string[] args)
    {
        var (scanned, problem) = Arguments.Scan(args);
        if (scanned is null)
        {
            return (null, problem);
        }

        if (scanned.Positional.Count != 1)
        {
            return (null, Cli.OneWorkloadProblem);
        }

        return scanned is { Environment: null, Catalog: null, Json: false, DryRun: false }
            ? (scanned.Positional[0], string.Empty)
            : (null, $"{Cli.ValidateCommand} takes only a workload file.");
    }
}
