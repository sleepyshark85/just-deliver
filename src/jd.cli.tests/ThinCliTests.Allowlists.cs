namespace jd.cli.tests;

// The references each thin-CLI rule finds today, by referring type. A slice that moves a rule out of the CLI removes its entries; a stale entry fails.
public sealed partial class ThinCliTests
{
    // R1 today: the formatters, which reach resolver and backend helpers to word a node's changes and a graph's config.
    private static readonly Dictionary<string, string[]> R1Allowlist = new()
    {
        ["jd.cli.DeployFormatter"] =
        [
            "jd.core.bp.DeploymentResult.ChangedOperations",
            "jd.core.bp.DeploymentResult.IsChange",
        ],
        ["jd.cli.PreviewFormatter"] =
        [
            "jd.resolver.expansion.ConfigLeaves.Of",
            "jd.resolver.expansion.ConfigObject..ctor",
            "jd.resolver.graph.ConfigJson.ToToken",
            "jd.resolver.policies.Provenance.Describe",
        ],
    };

    // R2 today: none; a command that named a port would be listed here.
    private static readonly Dictionary<string, string[]> R2Allowlist = [];

    // R3 today: the path form left, which validates a file (YamlSchemaValidator, until S25g).
    private static readonly Dictionary<string, string[]> R3Allowlist = new()
    {
        ["jd.definitionvalidator.YamlSchemaValidator"] =
        [
            "System.IO.File.ReadAllTextAsync",
        ],
    };
}
