using System.Globalization;
using jd.orchestrator.usecases;

namespace jd.cli;

/// <summary>
/// <c>jd approve</c>: reads the descriptor named on the command line, hands the label, the descriptor and the meaning to <see cref="Approve"/>, and
/// prints what it did or why it refused (docs/architecture/approvals.md section 4). Parsing, reading, formatting; no rules.
/// </summary>
internal static class ApproveCli
{
    private const string MeaningOption = "--meaning";

    private sealed record Options(string Label, string Environment, string Meaning);

    // args are the arguments after "approve".
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

        if (Arguments.Unreadable(options.Environment, null, null) is { } unreadable)
        {
            stderr.WriteLine($"jd: {unreadable}");
            return ExitCodes.UsageError;
        }

        var environment = await Cli.ReadDocumentAsync(options.Environment, cancellationToken);
        var result = await Approve.RunAsync(new ApproveInput(options.Label, environment, options.Meaning), connections, ShowIgnored(stderr), cancellationToken);
        Cli.Print(result.Errors, stderr);
        if (result.Problem is { } refusal)
        {
            stderr.WriteLine(Refusal(refusal));
        }

        if (result.Outcome is Outcome.InvalidInput)
        {
            stderr.Write(Cli.Usage);
        }

        if (result is { Environment: { Approval: { } policy } target, Record: { } record })
        {
            stdout.WriteLine($"approved {options.Label} for {target.Name} ({Number(result.Counted)} of {Number(policy.Required)})");
            stdout.WriteLine($"record: {record}");
        }

        return ExitCodes.Of(result.Outcome);
    }

    // Each approval file the use case skipped, as it skips it.
    private static Action<Progress> ShowIgnored(TextWriter stderr) => progress =>
    {
        if (progress is Ignored ignored)
        {
            stderr.WriteLine($"jd: {Cli.ApproveCommand}: ignored {ignored.What}");
        }
    };

    // The words of a refusal, around the problem's values.
    private static string Refusal(Problem problem) => problem switch
    {
        LabelInvalid invalid => $"jd: {invalid.Reason}",
        MeaningInvalid invalid => $"jd: {Cli.ApproveCommand} needs {MeaningOption} <text>: 1 to {Number(invalid.MaxLength)} characters, no control characters.",
        NotGated notGated => $"jd: {Cli.ApproveCommand}: environment '{notGated.Environment}' has no approval policy, so there is nothing to approve.",
        IdentityRefused refused => $"jd: {refused.Reason}",
        UnknownLabel unknown => $"jd: {Cli.ApproveCommand}: cannot approve {unknown.Label}: {string.Join("; ", unknown.Reasons)}",
        NotQualified notQualified => string.Join(
            Environment.NewLine,
            [$"jd: {Cli.ApproveCommand}: release {notQualified.Label} is not qualified on {notQualified.QualifiedOn}:", .. notQualified.Reasons.Select(reason => $"  {reason}")]),
        NotApprover notApprover => $"jd: {Cli.ApproveCommand}: principal {notApprover.Approver} is not listed in the approvers of '{notApprover.Environment}'.",
        NotSeparate notSeparate =>
            $"jd: {Cli.ApproveCommand}: principal {notSeparate.Approver} deployed {notSeparate.Label} on {notSeparate.QualifiedOn}, and '{notSeparate.Environment}' requires approvers separate from the deployer.",
        AlreadyApproved repeated => $"jd: {Cli.ApproveCommand}: principal {repeated.Approver} already approved {repeated.Label} for '{repeated.Environment}'.",
        RecordNotWritten notWritten => $"jd: {Cli.ApproveCommand}: the approval could not be written: {notWritten.Reason} If the file already exists, run the command again.",
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "not a refusal an approval meets."),
    };

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    // A missing meaning is passed on as none, which the use case refuses as it refuses an empty one.
    private static (Options? Options, string Problem) Parse(string[] args)
    {
        var (label, values, problem) = Arguments.ScanOneLabel(args, Arguments.EnvOption, MeaningOption);
        if (label is null)
        {
            return (null, problem);
        }

        return values.TryGetValue(Arguments.EnvOption, out var environment)
            ? (new Options(label, environment, values.GetValueOrDefault(MeaningOption, string.Empty)), string.Empty)
            : (null, $"{Cli.ApproveCommand} needs {Arguments.EnvOption}.");
    }
}
