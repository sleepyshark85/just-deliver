namespace jd.resolver.release;

/// <summary>The approval files of one label on a gated environment, as <see cref="ApprovalRecord.ReadAsync"/> read them.</summary>
/// <param name="Approvers">The distinct principals (case-insensitive) in the approvals that count as given for this set; whether each may approve is the policy's.</param>
/// <param name="Ignored">One entry per approval file that was not counted, naming the file and why.</param>
public sealed record ApprovalReading(IReadOnlyList<string> Approvers, IReadOnlyList<string> Ignored);
