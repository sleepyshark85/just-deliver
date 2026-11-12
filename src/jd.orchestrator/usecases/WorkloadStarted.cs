namespace jd.orchestrator.usecases;

/// <summary>A workload's walk starts; its <see cref="NodeFinished"/> events follow.</summary>
/// <param name="Workload">The workload's name.</param>
public sealed record WorkloadStarted(string Workload) : Progress;
