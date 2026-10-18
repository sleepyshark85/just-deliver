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

    private const string ToOption = "--to";
    private const string CatalogOption = "--catalog";

    // args are the arguments after "promote".
    public static async Task<int> RunAsync(
        string[] args, TextWriter stdout, TextWriter stderr, IBackEndProvider? backend, IRevisionProbe? probe, IDeployerIdentity? identity, ILiveRevisions? liveRevisions,
        TimeProvider time, Func<string, string?> environmentVariable, CancellationToken cancellationToken)
    {
        var (label, values, problem) = Arguments.ScanLabel(args, ToOption, CatalogOption);
        if (label is null || !values.TryGetValue(ToOption, out var to) || !values.TryGetValue(CatalogOption, out var catalog))
        {
            stderr.WriteLine($"jd: {(label is null ? problem : $"promote needs {ToOption} and {CatalogOption}.")}");
            stderr.Write(Cli.Usage);
            return UsageError;
        }

        if (Arguments.Unreadable(to, null, catalog) is { } unreadable)
        {
            stderr.WriteLine($"jd: {unreadable}");
            return UsageError;
        }

        var loaded = await EnvironmentFile.LoadAsync(to, cancellationToken);
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

        // A bad credential stops here, before any record is read.
        if (await Cli.GetIdentityAsync(identity, environmentVariable, stderr, cancellationToken) is not { } principal)
        {
            return Invalid;
        }

        var records = Cli.Records(environmentVariable);

        // Nothing below this check runs unless the label is qualified and approved; the walk reads the kept bytes and refuses them unless they hash to the checked set.
        var decision = await PromotionCheck.CheckAsync(records, target, label, cancellationToken);
        foreach (var ignored in decision.IgnoredApprovals)
        {
            stderr.WriteLine($"jd: promote: ignored {ignored}");
        }

        if (decision.SetSha256 is not { } setSha256 || !decision.MayPromote)
        {
            stderr.WriteLine($"jd: promote: release {label} cannot be promoted to '{target.Name}':");
            foreach (var reason in decision.Reasons)
            {
                stderr.WriteLine($"  {reason}");
            }

            return Invalid;
        }

        stdout.WriteLine($"promoting {label} from {policy.QualifiedOn} (set sha256 {setSha256})");
        // The kept set is named in messages as the records lay it out.
        return await ReleaseDeployCli.PromoteAsync(
            $"{policy.QualifiedOn}/{label}/{ReleaseRecordFile.SetFileName}", to, catalog, new ReleaseDeployCli.Promotion(principal, policy.QualifiedOn, label, setSha256), stdout, stderr, backend, probe, liveRevisions, time, environmentVariable, cancellationToken);
    }
}
