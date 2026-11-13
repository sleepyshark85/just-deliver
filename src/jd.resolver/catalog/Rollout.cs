namespace jd.resolver.catalog;

/// <summary>A runtime part's probe and release block, read where the part is (S24e binds them).</summary>
/// <param name="Runtime">The runtime that owns the part.</param>
/// <param name="Part">The part.</param>
/// <param name="Probe">How the release flow checks the part's runtime node.</param>
/// <param name="Release">The runtime node's inputs and outputs the release flow uses.</param>
/// <param name="IntervalLocation">Where the probe's interval is in the runtime's file.</param>
internal sealed record Rollout(PartOwner Runtime, Part Part, Probe Probe, RuntimeRelease Release, string IntervalLocation);
