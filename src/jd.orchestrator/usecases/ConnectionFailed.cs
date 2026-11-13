namespace jd.orchestrator.usecases;

/// <summary>A connection the run needs could not be built, so the run stopped before its first backend call.</summary>
/// <param name="Reason">The connection's own words.</param>
public sealed record ConnectionFailed(string Reason) : Problem;
