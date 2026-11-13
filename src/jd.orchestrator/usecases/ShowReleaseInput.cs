using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="ShowRelease"/> reads.</summary>
/// <param name="Set">The release set.</param>
public sealed record ShowReleaseInput(SourceDocument Set);
