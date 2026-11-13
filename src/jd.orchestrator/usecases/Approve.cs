using jd.core.identity;
using jd.resolver.environment;
using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>
/// <c>approve</c>: records that the principal of the credential approves the kept set of a label for a gated environment (docs/architecture/approvals.md
/// section 4). In this order: the label and the meaning; the descriptor and its policy; the principal; the set and its qualification; the approver;
/// the repeat; then the one write. Every check comes before the write, so a refusal leaves nothing behind.
/// </summary>
public static class Approve
{
    // The meaning's maxLength in schemas/approval-record.schema.json, which validates the record the meaning is written into.
    private const int MeaningMaxLength = 500;

    // An approval once the plain values, the descriptor and the principal have passed: who approves which label for which gated environment.
    private sealed record Approval(string Label, string Meaning, EnvironmentDescriptor Target, ApprovalPolicy Policy, DeployerIdentity Approver);

    /// <summary>Approves <paramref name="input"/>'s label for its environment, or returns the first refusal with nothing written.</summary>
    /// <param name="input">The label, the gated environment's descriptor and the meaning.</param>
    /// <param name="connections">The identity, the records and the clock.</param>
    /// <param name="progress">Told of each approval file skipped as one that cannot count.</param>
    /// <param name="cancellationToken">Cancels the approval.</param>
    public static async Task<ApproveResult> RunAsync(ApproveInput input, Connections connections, Action<Progress>? progress, CancellationToken cancellationToken)
    {
        if (ReleaseRecordFile.LabelProblem(input.Label) is { } labelProblem)
        {
            return Invalid(new LabelInvalid(labelProblem));
        }

        if (input.Meaning.Length is 0 or > MeaningMaxLength || input.Meaning.Any(char.IsControl))
        {
            return Invalid(new MeaningInvalid(MeaningMaxLength));
        }

        var loaded = await EnvironmentParser.LoadAsync(input.Environment, cancellationToken);
        if (loaded.Value is not { } target)
        {
            return new ApproveResult(loaded.Errors, null, Outcome.Failed, null, null, 0, null);
        }

        if (target.Approval is not { } policy)
        {
            return Refused(new NotGated(target.Name));
        }

        // A bad credential stops here, before any record is read.
        DeployerIdentity approver;
        try
        {
            approver = await connections.Identity().GetAsync(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Refused(new IdentityRefused(ex.Message));
        }

        return await ApproveAsync(new Approval(input.Label, input.Meaning, target, policy, approver), connections, progress, cancellationToken);
    }

    // The checks against the records, then the one write.
    private static async Task<ApproveResult> ApproveAsync(Approval approval, Connections connections, Action<Progress>? progress, CancellationToken cancellationToken)
    {
        var (label, meaning, target, policy, approver) = approval;
        var records = connections.Records();
        var qualification = await QualificationReader.ReadAsync(records, policy.QualifiedOn, label, cancellationToken);
        if (qualification.SetSha256 is not { } setSha256)
        {
            return Refused(new UnknownLabel(label, qualification.Reasons));
        }

        if (Refusal(approval, qualification) is { } refusal)
        {
            return Refused(refusal);
        }

        var earlier = await ApprovalRecord.ReadAsync(records, target, label, setSha256, cancellationToken);
        foreach (var ignored in earlier.Ignored)
        {
            progress?.Invoke(new Ignored(ignored));
        }

        if (earlier.Approvers.Contains(approver.ObjectId, StringComparer.OrdinalIgnoreCase))
        {
            return Refused(new AlreadyApproved(approver.ObjectId, label, target.Name));
        }

        var approvedAt = connections.Time().GetUtcNow();
        var json = ApprovalRecord.Build(target, label, setSha256, approver, meaning, approvedAt);
        string record;
        try
        {
            record = await ReleaseRecordFile.WriteAsync(records, target.Name, label, approvedAt, ReleaseRecordFile.ApproveActivity, json, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refused(new RecordNotWritten(ex.Message));
        }

        var counted = earlier.Approvers.Count(id => policy.Counts(id, qualification.Deployers)) + 1;
        return new ApproveResult([], null, Outcome.Succeeded, target, approver, counted, record);
    }

    // Why the approver may not approve the label's known set: it is not qualified, the approver is not listed, or the approver deployed it and the policy
    // separates; null when they may.
    private static Problem? Refusal(Approval approval, Qualification qualification)
    {
        var (label, _, target, policy, approver) = approval;
        if (!qualification.Qualified)
        {
            return new NotQualified(label, policy.QualifiedOn, qualification.Reasons);
        }

        if (!policy.Counts(approver.ObjectId, []))
        {
            return new NotApprover(approver.ObjectId, target.Name);
        }

        return policy.Counts(approver.ObjectId, qualification.Deployers) ? null : new NotSeparate(approver.ObjectId, label, policy.QualifiedOn, target.Name);
    }

    private static ApproveResult Invalid(Problem problem) => new([], problem, Outcome.InvalidInput, null, null, 0, null);

    private static ApproveResult Refused(Problem problem) => new([], problem, Outcome.Failed, null, null, 0, null);
}
