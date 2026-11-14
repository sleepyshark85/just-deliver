namespace jd.resolver.catalog;

/// <summary>A runtime part's probe and release block, as the binder read them where the part is, with what the load checks report.</summary>
/// <param name="Runtime">The runtime that owns the part.</param>
/// <param name="Part">The part.</param>
/// <param name="Probe">How the release flow checks the part's runtime node.</param>
/// <param name="Release">The runtime node's inputs and outputs the release flow uses, and how it names a revision.</param>
internal sealed record Rollout(PartOwner Runtime, Part Part, Probe Probe, RuntimeRelease Release)
{
    /// <summary>Where the probe's interval is in the runtime's file.</summary>
    public required string IntervalLocation { get; init; }

    /// <summary>The field that holds the probe's timeout, as errors name it.</summary>
    public required string TimeoutName { get; init; }

    /// <summary>The longest revision suffix the runtime accepts.</summary>
    public required int SuffixMaxLength { get; init; }

    /// <summary>Where <see cref="SuffixMaxLength"/> is in the runtime's file.</summary>
    public required string SuffixMaxLengthLocation { get; init; }
}
