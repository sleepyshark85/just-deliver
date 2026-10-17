using jd.resolver.environment;

namespace jd.resolver.release;

/// <summary>
/// Whether a label may be promoted to a gated environment (docs/architecture/approvals.md section 5, steps 1 to 4).
/// </summary>
/// <param name="SetSha256">SHA-256 of the label's kept <c>set.yaml</c> on the policy's <c>qualifiedOn</c> (<c>h</c>); null when the label is unknown or its records cannot be read.</param>
/// <param name="Reasons">Every reason the label may not be promoted; empty when it may.</param>
/// <param name="IgnoredApprovals">Approval files that were not counted, with why. Reported whatever the outcome: an ignored approval cannot block a promotion.</param>
public sealed record PromotionDecision(string? SetSha256, IReadOnlyList<string> Reasons, IReadOnlyList<string> IgnoredApprovals)
{
    public bool MayPromote => Reasons.Count == 0;
}

/// <summary>Judges a promotion from the files now; read-only, nothing is stored or repaired.</summary>
public static class PromotionCheck
{
    /// <summary>
    /// Qualified on the policy's <c>qualifiedOn</c> (<see cref="QualificationReader"/>) and approved by enough principals (<see cref="ApprovalPolicy.Evaluate"/>
    /// over <see cref="ApprovalRecord.ReadAsync"/>). Throws <see cref="ArgumentException"/> when <paramref name="target"/> has no approval policy or the label is
    /// not a single path segment.
    /// </summary>
    public static async Task<PromotionDecision> CheckAsync(string recordsDirectory, EnvironmentDescriptor target, string label, CancellationToken cancellationToken = default)
    {
        var policy = ApprovalRecord.Policy(target);
        var qualification = await QualificationReader.ReadAsync(recordsDirectory, policy.QualifiedOn, label, cancellationToken);
        if (qualification.SetSha256 is not { } setSha256)
        {
            return new PromotionDecision(null, qualification.Reasons, []);
        }

        var reasons = qualification.Reasons.Select(reason => $"not qualified on {policy.QualifiedOn}: {reason}").ToList();
        var approvals = await ApprovalRecord.ReadAsync(recordsDirectory, target, label, setSha256, cancellationToken);
        if (!policy.Evaluate(approvals.Approvers, qualification.Deployers))
        {
            var counting = policy.Counting(approvals.Approvers, qualification.Deployers).Count;
            reasons.Add($"{counting} of {policy.Required} required approvals for '{target.Name}' count.");
        }

        return new PromotionDecision(setSha256, reasons, approvals.Ignored);
    }
}
