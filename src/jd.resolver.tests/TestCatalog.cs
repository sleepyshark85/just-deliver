using jd.resolver.catalog;
using Xunit;

namespace jd.resolver.tests;

// Catalog snippets shared by the tests that build a small catalog in memory.
internal static class TestCatalog
{
    /// <summary>The probe and release block every runtime implementation needs, as top-level lines (<see cref="Runtime"/> indents them).</summary>
    public const string ProbeAndRelease =
        "probe: { path: /health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5, scheme: https }\n"
        + "release: { suffixInput: suffix, trafficInput: traffic, trafficOutput: traffic, suffixOutput: suffix, revisionOutput: revision, fqdnOutput: fqdn, revisionKey: revisionName, latestKey: latestRevision, weightKey: weight, resourceIdOutput: appId, suffix: { prefix: r, configDigits: 8, saltDigits: 6, maxLength: 20 }, fullWeight: 100 }\n";

    /// <summary>What <see cref="ProbeAndRelease"/> says, parsed.</summary>
    public static readonly Probe Probe = new("https", "/health", 200, 60, 5);

    public static readonly RuntimeRelease Release = new()
    {
        SuffixInput = "suffix",
        TrafficInput = "traffic",
        TrafficOutput = "traffic",
        SuffixOutput = "suffix",
        RevisionOutput = "revision",
        FqdnOutput = "fqdn",
        RevisionKey = "revisionName",
        LatestKey = "latestRevision",
        WeightKey = "weight",
        ResourceIdOutput = "appId",
        Suffix = new("r", 8, 6),
        FullWeight = 100,
    };

    /// <summary>The runtime every workload needs: one node named runtime. Append it after the other files so their indexes stay.</summary>
    public static readonly string RuntimeFile = Runtime("nodes:\n  runtime:\n    template: t/runtime\n    config: { k: 1 }\n");

    /// <summary>The grant targets of the test templates: t/g grants on its <c>scope</c> input.</summary>
    public const string GrantsFile = "kind: Grants\ntargets: { t/g: scope }\n";

    /// <summary>Where a component's part sits in its file, for <see cref="Component"/>'s defaults: errors in its nodes and exports are reported under it.</summary>
    public const string PartPath = "implementations.default.classes.standard";

    /// <summary>
    /// A Component file: <paramref name="part"/> (top-level <c>nodes:</c> and <c>exports:</c> lines) is the class <paramref name="className"/> of
    /// the implementation <paramref name="key"/>.
    /// </summary>
    public static string Component(string name, string exports, string part, string key = "default", string className = "standard") =>
        $"kind: Component\nname: {name}\ndescription: d\nexports: {exports}\nimplementations:\n  {key}:\n    classes:\n      {className}:\n{Indent(part, 8)}";

    /// <summary>The part of <paramref name="owner"/> at <paramref name="location"/> in its file.</summary>
    public static Part PartAt(PartOwner owner, string location) => Assert.Single(owner.Parts, part => part.Location == location);

    /// <summary>A Runtime file's lines up to its implementations; append <see cref="RuntimeImplementation"/>s.</summary>
    public const string RuntimeHeader = "kind: Runtime\nname: container-app\ndescription: d\nimplementations:\n";

    /// <summary>A Runtime file named container-app with one implementation <paramref name="key"/>.</summary>
    public static string Runtime(string body, string key = "default") => RuntimeHeader + RuntimeImplementation(body, key);

    /// <summary>The runtime implementation <paramref name="key"/>: <see cref="ProbeAndRelease"/>, then <paramref name="body"/> (top-level <c>nodes:</c> lines).</summary>
    public static string RuntimeImplementation(string body, string key = "default") => $"  {key}:\n{Indent(ProbeAndRelease + body, 4)}";

    private static string Indent(string text, int spaces) =>
        string.Join('\n', text.TrimEnd('\n').Split('\n').Select(line => line.Length == 0 ? line : new string(' ', spaces) + line)) + "\n";
}
