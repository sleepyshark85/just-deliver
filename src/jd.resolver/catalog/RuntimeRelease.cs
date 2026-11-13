namespace jd.resolver.catalog;

/// <summary>
/// What the release flow needs to know about the runtime node's template, so no template names are in code. Inputs the engine sets
/// per step: <see cref="SuffixInput"/>, <see cref="TrafficInput"/>. Outputs it reads: <see cref="TrafficOutput"/> (the traffic last
/// deployed, naming the live revision), <see cref="SuffixOutput"/> (the suffix last deployed), <see cref="RevisionOutput"/> (the revision this deploy created), <see cref="FqdnOutput"/> (its
/// address, the probe target), <see cref="ResourceIdOutput"/> (the runtime resource's id, which qualification looks its live revisions up by). Traffic items: <see cref="RevisionKey"/> names a revision, <see cref="LatestKey"/> means the latest one.
/// </summary>
public sealed record RuntimeRelease(
    string SuffixInput,
    string TrafficInput,
    string TrafficOutput,
    string SuffixOutput,
    string RevisionOutput,
    string FqdnOutput,
    string RevisionKey,
    string LatestKey,
    string WeightKey,
    string ResourceIdOutput);
