using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="DeployRelease"/> deploys.</summary>
/// <param name="Set">The release set.</param>
/// <param name="Environment">The environment descriptor.</param>
/// <param name="Catalog">The catalog, its templates part included.</param>
/// <param name="Preview">Only show the changes: nothing is recorded, and a gated environment allows it.</param>
public sealed record DeployReleaseInput(SourceDocument Set, SourceDocument Environment, SourceCatalog Catalog, bool Preview);
