namespace jd.resolver.release;

/// <summary>
/// What the records of one label on one environment say (docs/architecture/approvals.md section 5, step 2).
/// </summary>
/// <param name="SetSha256">SHA-256 of the label's kept <c>set.yaml</c>; null when the label's set cannot be known: an unknown label (no <c>set.yaml</c>), or a <c>set.yaml</c> or label directory that cannot be read. The <paramref name="Reasons"/> tell which.</param>
/// <param name="Reasons">Why the label is not qualified, one entry per problem; empty when it is.</param>
/// <param name="Deployers">The distinct principals that ran a <c>deploy</c> of the label, failed attempts included, from the records that passed the consistency checks.</param>
public sealed record Qualification(string? SetSha256, IReadOnlyList<string> Reasons, IReadOnlyList<string> Deployers)
{
    /// <summary>Whether the records show the label qualified: there is no reason it is not.</summary>
    public bool Qualified => Reasons.Count == 0;
}
