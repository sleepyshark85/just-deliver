using jd.orchestrator.usecases;

namespace jd.cli;

/// <summary>The process exit codes, as the usage text states them: the number the CLI shows for a use case's <see cref="Outcome"/>, and for its own usage errors.</summary>
internal static class ExitCodes
{
    /// <summary>The command did what it was asked.</summary>
    public const int Success = 0;

    /// <summary>Validation, resolution or deployment errors, or a refusal.</summary>
    public const int Failure = 1;

    /// <summary>The command line, or a file it names, cannot be used.</summary>
    public const int UsageError = 2;

    /// <summary>The exit code for <paramref name="outcome"/>.</summary>
    public static int Of(Outcome outcome) => outcome switch
    {
        Outcome.Succeeded => Success,
        Outcome.InvalidInput => UsageError,
        Outcome.Failed => Failure,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "not an outcome a use case returns."),
    };
}
