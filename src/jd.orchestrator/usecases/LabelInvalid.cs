namespace jd.orchestrator.usecases;

/// <summary>The label cannot name a release: it is not label-shaped, or not one path segment of the records.</summary>
/// <param name="Reason">The records' own words.</param>
public sealed record LabelInvalid(string Reason) : Problem;
