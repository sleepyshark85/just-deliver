using System.Diagnostics;

namespace jd.cli.tests;

/// <summary>
/// The <c>az</c> command line for one Azure test, logged in as the sandbox team identity (the <c>ARM_*</c> service principal that
/// Pulumi uses too), never as whoever is logged in on the machine: the login lives in a temporary <c>AZURE_CONFIG_DIR</c> that is
/// deleted with the session. The tests verify and clean up what they created through it.
/// </summary>
internal sealed class AzureCli : IDisposable
{
    private const string RunVariable = "JD_AZURE_TESTS";

    private static readonly string[] RequiredVariables = ["ARM_CLIENT_ID", "ARM_CLIENT_SECRET", "ARM_TENANT_ID", "ARM_SUBSCRIPTION_ID"];

    private readonly string _configDirectory;

    private AzureCli(string configDirectory, string subscription)
    {
        _configDirectory = configDirectory;
        Subscription = subscription;
    }

    public string Subscription { get; }

    /// <summary>Fails unless started by <c>tools/verify.sh --azure</c>, and with a message naming the missing variables; there is no fallback to an ambient <c>az</c> login.</summary>
    public static AzureCli Login()
    {
        // tools/verify.sh --azure sets this and serialises Azure runs (one free-tier Cosmos account per subscription); a plain
        // 'dotnet test' with the ARM_* identity in the environment must not reach Azure.
        if (System.Environment.GetEnvironmentVariable(RunVariable) != "1")
        {
            throw new InvalidOperationException($"The Azure tests create real resources and must be started with tools/verify.sh --azure (it sets {RunVariable}=1 and allows one run at a time).");
        }

        var values = RequiredVariables.ToDictionary(name => name, name => System.Environment.GetEnvironmentVariable(name));
        var missing = values.Where(v => string.IsNullOrEmpty(v.Value)).Select(v => v.Key).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"The Azure tests run as the sandbox team identity and need {string.Join(", ", missing)} in the environment (source the credentials file, see docs/engineering/standards.md section 6). They never use an existing az login.");
        }

        var directory = Directory.CreateTempSubdirectory("jd-az-").FullName;
        var session = new AzureCli(directory, values["ARM_SUBSCRIPTION_ID"]!);
        try
        {
            // The secret is passed with the --password= form and never echoed: only az's own error text is reported.
            var login = session.Start(["login", "--service-principal", "--username", values["ARM_CLIENT_ID"]!, $"--password={values["ARM_CLIENT_SECRET"]}", "--tenant", values["ARM_TENANT_ID"]!, "--output", "none"]);
            if (login.Code != 0)
            {
                throw new InvalidOperationException($"az login as the sandbox team identity failed: {login.Error}");
            }

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void Dispose() => Directory.Delete(_configDirectory, recursive: true);

    /// <summary>Runs <c>az</c> with these arguments (one string each, so a JMESPath query needs no quoting); the subscription is not added, so commands name it.</summary>
    public (int Code, string Output, string Error) Run(params string[] arguments) => Start(arguments);

    private (int Code, string Output, string Error) Start(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo("az") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["AZURE_CONFIG_DIR"] = _configDirectory;
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Both streams are read while the process runs, so a full pipe cannot block it.
        using var process = Process.Start(info) ?? throw new InvalidOperationException("could not start az");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.GetAwaiter().GetResult());
    }
}
