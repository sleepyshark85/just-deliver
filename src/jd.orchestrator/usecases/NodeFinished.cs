namespace jd.orchestrator.usecases;

/// <summary>A node of the walk is done, whatever its outcome.</summary>
/// <param name="Report">What happened to the node.</param>
public sealed record NodeFinished(NodeReport Report) : Progress;
