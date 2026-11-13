namespace jd.cli.tests;

// The references each thin-CLI rule finds today, by referring type. A slice that moves a rule out of the CLI removes its entries; a stale entry fails.
public sealed partial class ThinCliTests
{
    // R1 today: command code that runs the rules itself (Appendix A moves each into a use case) and reads Connections to build the orchestrator, the
    // identity, the records and the clock.
    private static readonly Dictionary<string, string[]> R1Allowlist = new()
    {
        ["jd.cli.ApproveCli"] =
        [
            "jd.core.identity.DeployerIdentity.get_ObjectId",
            "jd.orchestrator.usecases.Connections.get_Records",
            "jd.orchestrator.usecases.Connections.get_Time",
            "jd.resolver.LoadResult`1.get_Errors",
            "jd.resolver.LoadResult`1.get_Value",
            "jd.resolver.environment.ApprovalPolicy.Counts",
            "jd.resolver.environment.EnvironmentParser.LoadAsync",
            "jd.resolver.release.ApprovalReading.get_Approvers",
            "jd.resolver.release.ApprovalReading.get_Ignored",
            "jd.resolver.release.ApprovalRecord.Build",
            "jd.resolver.release.ApprovalRecord.ReadAsync",
            "jd.resolver.release.Qualification.get_Deployers",
            "jd.resolver.release.Qualification.get_Qualified",
            "jd.resolver.release.Qualification.get_Reasons",
            "jd.resolver.release.Qualification.get_SetSha256",
            "jd.resolver.release.QualificationReader.ReadAsync",
            "jd.resolver.release.ReleaseRecordFile.WriteAsync",
        ],
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
            "jd.orchestrator.usecases.Promotion..ctor",
            "jd.resolver.LoadResult`1.get_Errors",
            "jd.resolver.LoadResult`1.get_Value",
            "jd.resolver.environment.EnvironmentParser.LoadAsync",
            "jd.resolver.release.PromotionCheck.CheckAsync",
            "jd.resolver.release.PromotionDecision.get_IgnoredApprovals",
            "jd.resolver.release.PromotionDecision.get_MayPromote",
            "jd.resolver.release.PromotionDecision.get_Reasons",
            "jd.resolver.release.PromotionDecision.get_SetSha256",
        ],
        // The release run's start is a use-case step its command calls until S25e2; the walk, IQ and the record are the command's until then.
        ["jd.cli.ReleaseDeployCli"] =
        [
            "jd.orchestrator.IqCheck.get_Failure",
            "jd.orchestrator.IqCheck.get_Passed",
            "jd.orchestrator.IqCheck.get_Workload",
            "jd.orchestrator.IqResult.get_Checks",
            "jd.orchestrator.IqResult.get_Passed",
            "jd.orchestrator.Orchestrator.DeployReleaseAsync",
            "jd.orchestrator.Qualifier.QualifyAsync",
            "jd.orchestrator.ReleaseRecordBuilder.Build",
            "jd.orchestrator.ReleaseRecordBuilder.BuildQualify",
            "jd.orchestrator.WorkloadRun.get_Run",
            "jd.orchestrator.WorkloadRun.get_Workload",
            "jd.orchestrator.usecases.Recording.get_Identity",
            "jd.orchestrator.usecases.Recording.get_Qualifier",
            "jd.orchestrator.usecases.Recording.get_Records",
            "jd.orchestrator.usecases.Recording.get_SetSha256",
            "jd.orchestrator.usecases.ReleaseStart.StartAsync",
            "jd.orchestrator.usecases.ReleaseStartInput..ctor",
            "jd.orchestrator.usecases.ReleaseStartResult.get_Errors",
            "jd.orchestrator.usecases.ReleaseStartResult.get_Problem",
            "jd.orchestrator.usecases.ReleaseStartResult.get_Run",
            "jd.orchestrator.usecases.StartedRelease.Deconstruct",
            "jd.resolver.release.ReleaseRecordFile.WriteAsync",
        ],
    };

    // R2 today: the commands that build the records or the identity from the factories, or write the record of a started release run, so name the ports.
    private static readonly Dictionary<string, string[]> R2Allowlist = new()
    {
        ["jd.cli.ApproveCli"] =
        [
            "jd.core.records.IRecordStore",
        ],
        ["jd.cli.Cli"] =
        [
            "jd.core.identity.IDeployerIdentity",
        ],
        ["jd.cli.PromoteCli"] =
        [
            "jd.core.records.IRecordStore",
        ],
        ["jd.cli.ReleaseDeployCli"] =
        [
            "jd.core.records.IRecordStore",
        ],
    };

    // R3 today: the loaders that read the workload, the catalog, the descriptor and the release files from disk (the S25 slices hand them documents).
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
        ["jd.resolver.release.ReleaseSetFile"] =
        [
            "System.IO.File.Exists",
            "System.IO.File.ReadAllBytesAsync",
        ],
    };
}
