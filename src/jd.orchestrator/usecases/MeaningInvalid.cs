namespace jd.orchestrator.usecases;

/// <summary>The meaning of an approval is empty, longer than the record allows, or holds a control character (docs/architecture/approvals.md section 4).</summary>
/// <param name="MaxLength">The most characters a meaning may have.</param>
public sealed record MeaningInvalid(int MaxLength) : Problem;
