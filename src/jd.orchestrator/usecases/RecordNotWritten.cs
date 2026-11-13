namespace jd.orchestrator.usecases;

/// <summary>The record of what was done could not be written.</summary>
/// <param name="Reason">The records' own words.</param>
public sealed record RecordNotWritten(string Reason) : Problem;
