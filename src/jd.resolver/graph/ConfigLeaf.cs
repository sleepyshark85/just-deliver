using jd.resolver.expansion;
using Newtonsoft.Json.Linq;

namespace jd.resolver.graph;

/// <summary>One leaf of a node's config (<see cref="ConfigLeaves"/>), with where its value came from.</summary>
/// <param name="Path">The leaf's dotted path, its key in the node's provenance.</param>
/// <param name="Value">The leaf's value: anything but an object.</param>
/// <param name="Provenance">Where the value came from, in the one-line form of <see cref="policies.Provenance.Describe"/>; null when the node records none.</param>
public sealed record ConfigLeaf(string Path, ConfigValue Value, string? Provenance)
{
    /// <summary>The value's canonical JSON (<see cref="ConfigJson"/>).</summary>
    public JToken Json => ConfigJson.ToToken(Value);
}
