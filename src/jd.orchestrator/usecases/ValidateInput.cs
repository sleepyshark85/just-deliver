using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="Validate"/> checks.</summary>
/// <param name="Workload">The workload definition.</param>
public sealed record ValidateInput(SourceDocument Workload);
