namespace jd.orchestrator.usecases;

/// <summary>The set cannot be kept beside the label's records: the label holds another set, or cannot name a place in the records.</summary>
/// <param name="Reason">The records' own words.</param>
public sealed record RecordingRefused(string Reason) : Problem;
