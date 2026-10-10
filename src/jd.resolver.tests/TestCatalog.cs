using jd.resolver.catalog;

namespace jd.resolver.tests;

// Catalog snippets shared by the tests that build a small catalog in memory.
internal static class TestCatalog
{
    /// <summary>The probe and release block every runtime mapping needs, as mapping lines.</summary>
    public const string ProbeAndRelease =
        "probe: { path: /health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5 }\n"
        + "release: { suffixInput: suffix, trafficInput: traffic, trafficOutput: traffic, revisionOutput: revision, fqdnOutput: fqdn, revisionKey: revisionName, latestKey: latestRevision, weightKey: weight }\n";

    /// <summary>What <see cref="ProbeAndRelease"/> says, parsed.</summary>
    public static readonly Probe Probe = new("/health", 200, 60, 5);

    public static readonly RuntimeRelease Release = new("suffix", "traffic", "traffic", "revision", "fqdn", "revisionName", "latestRevision", "weight");

    /// <summary>The runtime mapping every workload needs: one node named runtime. Append it after the other files so their indexes stay.</summary>
    public const string RuntimeMapping = "kind: Mapping\nmatch: { kind: runtime }\n" + ProbeAndRelease + "nodes:\n  runtime:\n    template: t/runtime\n    config: { k: 1 }\n";
}
