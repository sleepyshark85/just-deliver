using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="ReleaseStart"/> starts: a set, or a promotion, whose set is the one kept on the records.</summary>
/// <param name="Set">The release set; null exactly for a promotion.</param>
/// <param name="Environment">The environment descriptor.</param>
/// <param name="Catalog">The catalog, its templates part included.</param>
/// <param name="Preview">Only show the changes: nothing is recorded, and a gated environment allows it. A promotion is never a preview.</param>
/// <param name="Promotion">The promotion whose walk this is; null for a set given.</param>
public sealed record ReleaseStartInput(SourceDocument? Set, SourceDocument Environment, SourceCatalog Catalog, bool Preview, Promotion? Promotion);
