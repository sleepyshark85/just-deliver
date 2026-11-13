namespace jd.orchestrator.usecases;

/// <summary>The record could not be written; what was done stays done.</summary>
/// <param name="Reason">The records' own words.</param>
public sealed record RecordNotWritten(string Reason) : Problem;
