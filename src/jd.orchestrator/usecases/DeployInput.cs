using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="Deploy"/> deploys.</summary>
/// <param name="Workload">The workload definition.</param>
/// <param name="Environment">The environment descriptor.</param>
/// <param name="Catalog">The catalog, its templates part included.</param>
/// <param name="Preview">Only show the changes: nothing is created, and a gated environment allows it.</param>
public sealed record DeployInput(SourceDocument Workload, SourceDocument Environment, SourceCatalog Catalog, bool Preview);
