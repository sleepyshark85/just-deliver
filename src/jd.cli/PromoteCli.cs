using jd.core.bp;
using jd.core.identity;
using jd.core.revisions;
using jd.orchestrator;
using jd.resolver.environment;
using jd.resolver.release;

namespace jd.cli;

/// <summary>
/// <c>jd promote</c>: deploys the set kept for a label on the policy's source environment to a gated environment (docs/architecture/approvals.md section 5). It judges
/// the promotion from the records, then lets the release walk of <see cref="ReleaseDeployCli"/> deploy the kept bytes, so a promotion is deployed, recorded and
/// qualified as a <c>release deploy</c> is. A refusal calls no backend and writes nothing. Wiring and output only; exit codes as <see cref="Cli"/>.
/// </summary>
internal static class PromoteCli
{
    private const int Invalid = 1;
    private const int UsageError = 2;

    private sealed record Options(string Label, string To, string Catalog);

    // args are the arguments after "promote".
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, IBackEndProvider? backend, IRevisionProbe? probe, IDeployerIdentity? identity, ILiveRevisions? liveRevisions,
        TimeProvider time, Func<string, string?> environmentVariable, CancellationToken cancellationToken)
    {
        var (options, problem) = Parse(args);
        if (options is null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Cli.Usage);
            return UsageError;
        }

        var records = environmentVariable(Cli.RecordsDirectoryVariable) ?? string.Empty;
        problem = Arguments.Unreadable(options.To, null, options.Catalog) ?? Cli.RecordsDirectoryProblem(records);
        if (problem is not null)
        {
            stderr.WriteLine($"jd: {problem}");
            return UsageError;
        }

        var loaded = await EnvironmentFile.LoadAsync(options.To, cancellationToken);
        if (loaded.Descriptor is not { } target)
        {
            Cli.Print(loaded.Errors, stderr);
            return Invalid;
        }

        if (target.Approval is not { } policy)
        {
            stderr.WriteLine($"jd: promote: environment '{target.Name}' has no approval policy, so there is nothing to promote through.");
            return Invalid;
        }

        // Nothing below this check runs unless the label is qualified and approved; the walk gets the kept bytes, so it can deploy no other set.
        var decision = await PromotionCheck.CheckAsync(records, target, options.Label, cancellationToken);
        foreach (var ignored in decision.IgnoredApprovals)
        {
            stderr.WriteLine($"jd: promote: ignored {ignored}");
        }

        if (decision.SetSha256 is not { } setSha256 || !decision.MayPromote)
        {
            stderr.WriteLine($"jd: promote: release {options.Label} cannot be promoted to '{target.Name}':");
            foreach (var reason in decision.Reasons)
            {
                stderr.WriteLine($"  {reason}");
            }

            return Invalid;
        }

        stdout.WriteLine($"promoting {options.Label} from {policy.QualifiedOn} (set sha256 {setSha256})");
        var kept = ReleaseRecordFile.KeptSetPath(records, policy.QualifiedOn, options.Label);
        return await ReleaseDeployCli.RunAsync(
            [kept, "--env", options.To, "--catalog", options.Catalog], qualify: false, stdout, stderr, backend, probe, identity, liveRevisions, time, environmentVariable, cancellationToken, promoted: true);
    }

    private static (Options? Options, string Problem) Parse(string[] args)
    {
        string? to = null, catalog = null;
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--to" or "--catalog" when i + 1 == args.Length:
                    return (null, $"{args[i]} needs a value.");
                case "--to":
                    to = args[++i];
                    break;
                case "--catalog":
                    catalog = args[++i];
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

        if (ReleaseRecordFile.LabelProblem(positional[0]) is { } labelProblem)
        {
            return (null, labelProblem);
        }

        return to is null || catalog is null ? (null, "promote needs --to and --catalog.") : (new Options(positional[0], to, catalog), string.Empty);
    }
}
