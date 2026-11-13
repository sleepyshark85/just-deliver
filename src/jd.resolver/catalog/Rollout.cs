using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>A runtime part's probe and release block, read where the part is (S24e binds them).</summary>
/// <param name="Runtime">The runtime that owns the part.</param>
/// <param name="Part">The part.</param>
/// <param name="Probe">How the release flow checks the part's runtime node.</param>
/// <param name="Release">The runtime node's inputs and outputs the release flow uses.</param>
/// <param name="IntervalLocation">Where the probe's interval is in the runtime's file.</param>
internal sealed record Rollout(PartOwner Runtime, Part Part, Probe Probe, RuntimeRelease Release, string IntervalLocation)
{
    /// <summary>The rollout of <paramref name="part"/>, which <paramref name="runtime"/> read from <paramref name="document"/>. Read by its fields until S24e binds them.</summary>
    public static Rollout Read(Document document, PartOwner runtime, Part part)
    {
        // Safe: only schema-valid documents are bound, and runtime.schema.json requires `probe`, `release` and all four probe fields.
        var implementation = document.Body.SelectToken(part.Location)!;
        var probe = implementation["probe"]!;
        var release = implementation["release"]!;
        var interval = probe["intervalSeconds"]!;
        return new Rollout(
            runtime,
            part,
            new Probe(Text(probe["path"]), (int)probe["expectedStatus"]!, (int)probe["timeoutSeconds"]!, (int)interval),
            new RuntimeRelease(
                Text(release["suffixInput"]), Text(release["trafficInput"]), Text(release["trafficOutput"]), Text(release["suffixOutput"]), Text(release["revisionOutput"]),
                Text(release["fqdnOutput"]), Text(release["revisionKey"]), Text(release["latestKey"]), Text(release["weightKey"]), Text(release["resourceIdOutput"])),
            interval.Path);
    }

    private static string Text(JToken? token) => (string?)token ?? string.Empty;
}
