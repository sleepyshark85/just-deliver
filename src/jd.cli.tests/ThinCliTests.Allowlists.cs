namespace jd.cli.tests;

// The references each thin-CLI rule finds today, by referring type. A slice that moves a rule out of the CLI removes its entries; a stale entry fails.
public sealed partial class ThinCliTests
{
    // R1 today: command code that runs the rules itself (Appendix A moves each into a use case) and reads Connections to build the orchestrator, the
    // identity, the records and the clock.
    private static readonly Dictionary<string, string[]> R1Allowlist = new()
    {
        ["jd.cli.Arguments"] =
        [
            "jd.resolver.release.ReleaseRecordFile.LabelProblem",
        ],
        ["jd.cli.Cli"] =
        [
            "jd.core.identity.IDeployerIdentity.GetAsync",
            "jd.orchestrator.usecases.Connections.get_Identity",
        ],
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
        ["jd.cli.PromoteCli"] =
        [
            "jd.orchestrator.usecases.Connections.get_Records",
            "jd.resolver.LoadResult`1.get_Errors",
            "jd.resolver.LoadResult`1.get_Value",
            "jd.resolver.environment.EnvironmentParser.LoadAsync",
            "jd.resolver.release.PromotionCheck.CheckAsync",
            "jd.resolver.release.PromotionDecision.get_IgnoredApprovals",
            "jd.resolver.release.PromotionDecision.get_MayPromote",
            "jd.resolver.release.PromotionDecision.get_Reasons",
            "jd.resolver.release.PromotionDecision.get_SetSha256",
        ],
    };

    // R2 today: the commands that build the records or the identity from the factories, so name the ports.
    private static readonly Dictionary<string, string[]> R2Allowlist = new()
    {
        ["jd.cli.Cli"] =
        [
            "jd.core.identity.IDeployerIdentity",
        ],
        ["jd.cli.PromoteCli"] =
        [
            "jd.core.records.IRecordStore",
        ],
    };

    // R3 today: the path forms left, which read the descriptor from disk (DocumentLoader, until S25f2) and validate a file (YamlSchemaValidator, until S25g).
    private static readonly Dictionary<string, string[]> R3Allowlist = new()
    {
        ["jd.definitionvalidator.YamlSchemaValidator"] =
        [
            "System.IO.File.ReadAllTextAsync",
        ],
        ["jd.resolver.DocumentLoader"] =
        [
            "System.IO.File.Exists",
            "System.IO.File.ReadAllTextAsync",
        ],
    };
}
