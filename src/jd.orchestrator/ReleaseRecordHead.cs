using jd.core.identity;
using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.release;

namespace jd.orchestrator;

/// <summary>What every release record starts with: when, who, where, and which release (<see cref="ReleaseRecordBuilder"/>).</summary>
/// <param name="Set">The release of the run; its workloads come out in deploy order.</param>
/// <param name="SetSha256">SHA-256 of the kept <c>set.yaml</c>.</param>
/// <param name="Environment">The environment of the run.</param>
/// <param name="Graphs">The resolved graph of each workload.</param>
/// <param name="Identity">The principal the run authenticated with, from the identity port.</param>
/// <param name="StartedAt">When the activity started.</param>
/// <param name="FinishedAt">When it finished.</param>
public sealed record ReleaseRecordHead(
    ReleaseSet Set, string SetSha256, EnvironmentDescriptor Environment, IReadOnlyList<ResolvedGraph> Graphs, DeployerIdentity Identity,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt);
