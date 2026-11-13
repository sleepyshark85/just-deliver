using jd.resolver;
using jd.resolver.environment;
using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>
/// <c>promote</c>: deploys the set kept for a label on the policy's source environment to a gated environment (docs/architecture/approvals.md
/// section 5). In this order: the label; the target and its policy; the principal; the qualification and the approvals, read from the records; then the
/// walk of <see cref="DeployRelease"/> on the kept bytes, so a promotion is deployed, recorded and qualified as a release deploy is. A refusal before
/// the walk builds no backend and writes nothing.
/// </summary>
public static class Promote
{
    /// <summary>Promotes <paramref name="input"/>'s label to its target, or returns the first refusal with nothing deployed.</summary>
    /// <param name="input">The label, the gated environment's descriptor and the catalog.</param>
    /// <param name="connections">The identity, the records, and what the walk reaches.</param>
    /// <param name="progress">Told of each approval file skipped, that the walk starts, then as each workload starts and each of its nodes finishes.</param>
    /// <param name="cancellationToken">Cancels the promotion.</param>
    public static async Task<PromoteResult> RunAsync(PromoteInput input, Connections connections, Action<Progress>? progress, CancellationToken cancellationToken)
    {
        if (ReleaseRecordFile.LabelProblem(input.Label) is { } labelProblem)
        {
            return new([], new LabelInvalid(labelProblem), Outcome.InvalidInput, null, null, null);
        }

        var loaded = await EnvironmentParser.LoadAsync(input.Target, cancellationToken);
        if (loaded.Value is not { } target)
        {
            return new(loaded.Errors, null, Outcome.Failed, null, null, null);
        }

        if (target.Approval is not { } policy)
        {
            return Refused(new NotGated(target.Name));
        }

        // A bad credential stops here, before any record is read.
        var (principal, identityRefusal) = await Checks.PrincipalAsync(connections, cancellationToken);
        if (principal is null)
        {
            return Refused(identityRefusal!); // a credential that names no principal comes with its refusal
        }

        var records = connections.Records();
        var decision = await PromotionCheck.CheckAsync(records, target, input.Label, cancellationToken);
        foreach (var ignored in decision.IgnoredApprovals)
        {
            progress?.Invoke(new Ignored(ignored));
        }

        if (decision.SetSha256 is not { } setSha256 || !decision.MayPromote)
        {
            return Refused(new NotPromotable(input.Label, target.Name, decision.Reasons));
        }

        progress?.Invoke(new Promoting(input.Label, policy.QualifiedOn, setSha256));

        // The bytes kept on the source are the set deployed; the walk refuses them unless they hash to the set judged above.
        var kept = new SourceDocument(
            ReleaseRecordFile.KeptSetName(policy.QualifiedOn, input.Label),
            await records.ReadAsync(policy.QualifiedOn, input.Label, ReleaseRecordFile.SetFileName, cancellationToken));
        var release = await DeployRelease.WalkAsync(
            new DeployReleaseInput(kept, input.Target, input.Catalog, false), new Promotion(principal, setSha256), connections, progress, cancellationToken);
        return new([], null, release.Outcome, target, setSha256, release);
    }

    private static PromoteResult Refused(Problem problem) => new([], problem, Outcome.Failed, null, null, null);
}
