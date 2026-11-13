using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="Promote"/> promotes.</summary>
/// <param name="Label">The release label whose kept set is promoted.</param>
/// <param name="Target">The gated environment's descriptor.</param>
/// <param name="Catalog">The catalog, its templates part included.</param>
public sealed record PromoteInput(string Label, SourceDocument Target, SourceCatalog Catalog);
