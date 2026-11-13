using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>A release run that passed every check, with everything it calls built; nothing is deployed yet.</summary>
/// <param name="Set">The release set.</param>
/// <param name="Environment">The environment.</param>
/// <param name="Graphs">Each workload's graph, in deploy order.</param>
/// <param name="Orchestrator">Walks the graphs over the backend.</param>
/// <param name="Recording">Where the run is recorded, and its qualifier; null exactly for a preview.</param>
/// <param name="Time">The run's clock.</param>
/// <param name="Started">When the run started.</param>
internal sealed record StartedRelease(
    ReleaseSet Set, EnvironmentDescriptor Environment, IReadOnlyList<ResolvedGraph> Graphs, Orchestrator Orchestrator, Recording? Recording, TimeProvider Time, DateTimeOffset Started);
