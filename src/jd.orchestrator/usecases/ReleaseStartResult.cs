using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>How <see cref="ReleaseStart"/> ended: the run started, or why it did not.</summary>
/// <param name="Errors">Every problem found in the documents, the resolution or the templates, each at its document and location.</param>
/// <param name="Problem">The refusal that stopped the start; null when none did.</param>
/// <param name="Run">The started run; null when there are errors or a problem.</param>
public sealed record ReleaseStartResult(IReadOnlyList<LoadError> Errors, Problem? Problem, StartedRelease? Run);
