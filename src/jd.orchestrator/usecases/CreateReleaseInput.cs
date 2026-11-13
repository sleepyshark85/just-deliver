using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="CreateRelease"/> builds a set from.</summary>
/// <param name="Manifest">The release manifest.</param>
/// <param name="Definitions">Reads each workload definition the manifest names; its name is what the definition's errors print.</param>
public sealed record CreateReleaseInput(SourceDocument Manifest, DocumentReader Definitions);
