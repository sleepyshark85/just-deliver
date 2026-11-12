using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="Preview"/> resolves.</summary>
/// <param name="Workload">The workload definition.</param>
/// <param name="Environment">The environment descriptor.</param>
/// <param name="Catalog">The catalog.</param>
public sealed record PreviewInput(SourceDocument Workload, SourceDocument Environment, SourceCatalog Catalog);
