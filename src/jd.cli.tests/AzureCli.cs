using System.Diagnostics;

namespace jd.cli.tests;

/// <summary>Runs the <c>az</c> command line for the Azure tests, which verify and clean up what they created through it.</summary>
internal static class AzureCli
{
    // Both streams are read while the process runs, so a full pipe cannot block it.
    public static (int Code, string Output, string Error) Run(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("az", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })
            ?? throw new InvalidOperationException("could not start az");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.GetAwaiter().GetResult());
    }
}
