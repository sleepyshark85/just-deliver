using System.Globalization;
using jd.core.identity;
using jd.resolver.environment;
using jd.resolver.release;

namespace jd.cli;

/// <summary>
/// <c>jd approve</c>: records that the principal of the credential approves the kept set of a label for a gated environment (docs/architecture/approvals.md
/// section 4). Everything is checked before the one write, so a refusal leaves nothing behind. Wiring and output only; exit codes as <see cref="Cli"/>.
/// </summary>
internal static class ApproveCli
{
    private const int Success = 0;
    private const int Invalid = 1;
    private const int UsageError = 2;

    // The schema's maxLength for a meaning (schemas/approval-record.schema.json).
    private const int MeaningMaxLength = 500;

    private sealed record Options(string Label, string Environment, string Meaning);

    // What an approval is checked against: the gated environment, its policy and where the records are.
    private sealed record Request(Options Options, EnvironmentDescriptor Target, ApprovalPolicy Policy, string Records);

    // args are the arguments after "approve".
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, IDeployerIdentity? identity, TimeProvider time, Func<string, string?> environmentVariable, CancellationToken cancellationToken)
    {
        var (options, problem) = Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Cli.Usage);
            return UsageError;
        }

        if (Arguments.Unreadable(options.Environment, null, null) is { } unreadable)
        {
            stderr.WriteLine($"jd: {unreadable}");
            return UsageError;
        }

        var loaded = await EnvironmentFile.LoadAsync(options.Environment, cancellationToken);
        if (loaded.Descriptor is not { } target)
        {
            Cli.Print(loaded.Errors, stderr);
            return Invalid;
        }

        if (target.Approval is not { } policy)
        {
            stderr.WriteLine($"jd: approve: environment '{target.Name}' has no approval policy, so there is nothing to approve.");
            return Invalid;
        }

        var records = environmentVariable(ReleaseDeployCli.RecordsDirectoryVariable) ?? string.Empty;
        problem = ReleaseDeployCli.RecordsDirectoryProblem(records) ?? LabelProblem(records, target.Name, options.Label);
        if (problem is not null)
        {
            stderr.WriteLine($"jd: {problem}");
            return UsageError;
        }

        try
        {
            // A bad credential stops here, before any record is read.
            return await Cli.GetIdentityAsync(identity, environmentVariable, stderr, cancellationToken) is { } principal
                ? await ApproveAsync(new Request(options, target, policy, records), principal, time, stdout, stderr, cancellationToken)
                : Invalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return UsageError;
        }
    }

    private static async Task<int> ApproveAsync(Request request, DeployerIdentity principal, TimeProvider time, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var (options, target, policy, records) = request;
        var label = options.Label;
        var qualification = await QualificationReader.ReadAsync(records, policy.QualifiedOn, label, cancellationToken);
        if (qualification.SetSha256 is not { } setSha256)
        {
            // The reason says whether the label is unknown or its records cannot be read.
            stderr.WriteLine($"jd: approve: cannot approve {label}: {string.Join("; ", qualification.Reasons)}");
            return Invalid;
        }

        if (!qualification.Qualified)
        {
            stderr.WriteLine($"jd: approve: release {label} is not qualified on {policy.QualifiedOn}:");
            foreach (var reason in qualification.Reasons)
            {
                stderr.WriteLine($"  {reason}");
            }

            return Invalid;
        }

        var approver = principal.ObjectId;
        var earlier = await ApprovalRecord.ApproversAsync(records, target, label, setSha256, cancellationToken);
        var refusal = !policy.Counts(approver, []) ? $"principal {approver} is not listed in the approvers of '{target.Name}'."
            : !policy.Counts(approver, qualification.Deployers) ? $"principal {approver} deployed {label} on {policy.QualifiedOn}, and '{target.Name}' requires approvers separate from the deployer."
            : earlier.Contains(approver, StringComparer.OrdinalIgnoreCase) ? $"principal {approver} already approved {label} for '{target.Name}'."
            : null;
        if (refusal is not null)
        {
            stderr.WriteLine($"jd: approve: {refusal}");
            return Invalid;
        }

        var approvedAt = time.GetUtcNow();
        var json = ApprovalRecord.Build(target, label, setSha256, principal, options.Meaning, approvedAt);
        string path;
        try
        {
            path = await ReleaseRecordFile.WriteAsync(records, target.Name, label, approvedAt, ReleaseRecordFile.ApproveActivity, json, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"jd: approve: the approval could not be written: {ex.Message} If the file already exists, run the command again.");
            return Invalid;
        }

        var counted = earlier.Count(id => policy.Counts(id, qualification.Deployers)) + 1;
        stdout.WriteLine($"approved {label} for {target.Name} ({counted.ToString(CultureInfo.InvariantCulture)} of {policy.Required.ToString(CultureInfo.InvariantCulture)})");
        stdout.WriteLine($"record: {path}");
        return Success;
    }

    // The label names a directory of the store; the store's own rule, applied before the credential so that a bad label is a usage error.
    private static string? LabelProblem(string records, string environment, string label)
    {
        try
        {
            _ = ReleaseRecordFile.LabelDirectory(records, environment, label);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        string? environment = null, meaning = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--env" or "--meaning" when i + 1 == args.Length:
                    return (null, $"{args[i]} needs a value.");
                case "--env":
                    environment = args[++i];
                    break;
                case "--meaning":
                    meaning = args[++i];
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
            return (null, "expected exactly one release label.");
        }

        if (environment is null)
        {
            return (null, "approve needs --env.");
        }

        return meaning is null || meaning.Length is 0 or > MeaningMaxLength || meaning.Any(char.IsControl)
            ? (null, $"approve needs --meaning <text>: 1 to {MeaningMaxLength.ToString(CultureInfo.InvariantCulture)} characters, no control characters.")
            : (new Options(positional[0], environment, meaning), string.Empty);
    }
}
