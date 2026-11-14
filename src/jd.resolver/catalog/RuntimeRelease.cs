namespace jd.resolver.catalog;

/// <summary>
/// What the release flow needs to know about the runtime node's template and its revisions, so none of it is in code. Inputs the engine sets
/// per step: <see cref="SuffixInput"/>, <see cref="TrafficInput"/>. Outputs it reads: <see cref="TrafficOutput"/>, <see cref="SuffixOutput"/>,
/// <see cref="RevisionOutput"/>, <see cref="FqdnOutput"/>, <see cref="ResourceIdOutput"/>. A traffic item: <see cref="RevisionKey"/>,
/// <see cref="LatestKey"/>, <see cref="WeightKey"/>.
/// </summary>
public sealed record RuntimeRelease
{
    /// <summary>The input that receives the revision suffix.</summary>
    public required string SuffixInput { get; init; }

    /// <summary>The input that receives the traffic list.</summary>
    public required string TrafficInput { get; init; }

    /// <summary>The output echoing the traffic last deployed, which names the live revision.</summary>
    public required string TrafficOutput { get; init; }

    /// <summary>The output echoing the suffix last deployed.</summary>
    public required string SuffixOutput { get; init; }

    /// <summary>The output naming the revision a deploy created.</summary>
    public required string RevisionOutput { get; init; }

    /// <summary>The output with that revision's address, the probe target.</summary>
    public required string FqdnOutput { get; init; }

    /// <summary>The output with the runtime resource's id, which qualification looks its live revisions up by.</summary>
    public required string ResourceIdOutput { get; init; }

    /// <summary>The key of a traffic item that names a revision.</summary>
    public required string RevisionKey { get; init; }

    /// <summary>The key of a traffic item that means the latest revision.</summary>
    public required string LatestKey { get; init; }

    /// <summary>The key of a traffic item's weight.</summary>
    public required string WeightKey { get; init; }

    /// <summary>How a revision suffix is made.</summary>
    public required RevisionSuffix Suffix { get; init; }

    /// <summary>The weight that sends all traffic to one revision.</summary>
    public required int FullWeight { get; init; }
}
