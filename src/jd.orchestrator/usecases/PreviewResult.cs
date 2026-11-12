using jd.resolver;
using jd.resolver.graph;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="Preview"/> resolved.</summary>
/// <param name="Errors">Every problem found, each at its document and location; empty when the workload resolved.</param>
/// <param name="Outcome"><see cref="Outcome.Succeeded"/> when there are no errors, else <see cref="Outcome.Failed"/>.</param>
/// <param name="Graph">The resolved graph; null when there are errors.</param>
public sealed record PreviewResult(IReadOnlyList<LoadError> Errors, Outcome Outcome, ResolvedGraph? Graph);
