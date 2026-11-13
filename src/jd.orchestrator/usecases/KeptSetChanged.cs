namespace jd.orchestrator.usecases;

/// <summary>A promotion's kept set no longer hashes to the set its promotion judged (docs/architecture/approvals.md section 5).</summary>
/// <param name="Set">The kept set's name in the records.</param>
/// <param name="ExpectedSha256">The hash the promotion judged.</param>
public sealed record KeptSetChanged(string Set, string ExpectedSha256) : Problem;
