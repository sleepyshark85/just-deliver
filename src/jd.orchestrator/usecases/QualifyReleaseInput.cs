using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="QualifyRelease"/> checks.</summary>
/// <param name="Set">The release set.</param>
/// <param name="Environment">The environment descriptor.</param>
/// <param name="Catalog">The catalog, its templates part included.</param>
public sealed record QualifyReleaseInput(SourceDocument Set, SourceDocument Environment, SourceCatalog Catalog);
