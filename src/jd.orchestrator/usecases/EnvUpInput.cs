using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="EnvUp"/> provisions.</summary>
/// <param name="Definition">The environment definition.</param>
/// <param name="Catalog">The catalog, its templates part included.</param>
/// <param name="Base">The descriptor of the layer below, whose values the new ones are added to; null for none.</param>
public sealed record EnvUpInput(SourceDocument Definition, SourceCatalog Catalog, SourceDocument? Base);
