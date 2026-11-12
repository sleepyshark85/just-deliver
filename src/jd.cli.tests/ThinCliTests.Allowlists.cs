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
            "jd.resolver.environment.ApprovalPolicy.get_QualifiedOn",
            "jd.resolver.environment.ApprovalPolicy.get_Required",
            "jd.resolver.environment.EnvironmentDescriptor.get_Approval",
            "jd.resolver.environment.EnvironmentDescriptor.get_Name",
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
        ["jd.cli.CatalogFiles"] =
        [
            "jd.resolver.LoadError.<Clone>$",
            "jd.resolver.LoadError.set_File",
        ],
        ["jd.cli.Cli"] =
        [
            "jd.core.identity.IDeployerIdentity.GetAsync",
            "jd.orchestrator.Orchestrator..ctor",
            "jd.orchestrator.Orchestrator.DeployAsync",
            "jd.orchestrator.usecases.Checks.Templates",
            "jd.orchestrator.usecases.Connections.get_Backend",
            "jd.orchestrator.usecases.Connections.get_Identity",
            "jd.orchestrator.usecases.Connections.get_Probe",
        ],
        ["jd.cli.DeployFormatter"] =
        [
            "jd.core.bp.DeploymentResult.ChangedOperations",
            "jd.core.bp.DeploymentResult.IsChange",
        ],
        ["jd.cli.EnvCli"] =
        [
            "jd.orchestrator.usecases.Checks.EnvironmentType",
            "jd.resolver.LoadResult`1.get_Errors",
            "jd.resolver.LoadResult`1.get_Value",
            "jd.resolver.Resolver.ResolveSubstrate",
            "jd.resolver.catalog.CatalogParser.ParseAsync",
            "jd.resolver.environment.ComposedDescriptor..ctor",
            "jd.resolver.environment.ComposedDescriptor.get_Errors",
            "jd.resolver.environment.ComposedDescriptor.get_Yaml",
            "jd.resolver.environment.DescriptorComposer..ctor",
            "jd.resolver.environment.DescriptorComposer.ComposeAsync",
            "jd.resolver.environment.EnvironmentDefinition.Over",
            "jd.resolver.environment.EnvironmentDefinitionFile.LoadAsync",
            "jd.resolver.environment.EnvironmentDescriptor.get_Name",
            "jd.resolver.environment.EnvironmentDescriptor.get_Region",
            "jd.resolver.environment.EnvironmentDescriptor.get_Type",
            "jd.resolver.environment.EnvironmentParser.LoadAsync",
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
            "jd.resolver.environment.ApprovalPolicy.get_QualifiedOn",
            "jd.resolver.environment.EnvironmentDescriptor.get_Approval",
            "jd.resolver.environment.EnvironmentDescriptor.get_Name",
            "jd.resolver.environment.EnvironmentParser.LoadAsync",
            "jd.resolver.release.PromotionCheck.CheckAsync",
            "jd.resolver.release.PromotionDecision.get_IgnoredApprovals",
            "jd.resolver.release.PromotionDecision.get_MayPromote",
            "jd.resolver.release.PromotionDecision.get_Reasons",
            "jd.resolver.release.PromotionDecision.get_SetSha256",
        ],
        ["jd.cli.ReleaseCli"] =
        [
            "jd.resolver.release.ReleaseBuilder.BuildAsync",
            "jd.resolver.release.ReleaseSet.get_CreatedAt",
            "jd.resolver.release.ReleaseSet.get_Label",
            "jd.resolver.release.ReleaseSet.get_Order",
            "jd.resolver.release.ReleaseSet.get_Workloads",
            "jd.resolver.release.ReleaseSetFile.LoadAsync",
            "jd.resolver.release.ReleaseSetFile.WriteAsync",
            "jd.resolver.release.ReleaseSetResult.get_Errors",
            "jd.resolver.release.ReleaseSetResult.get_Set",
            "jd.resolver.release.ReleaseWorkload.get_DefinitionSha256",
            "jd.resolver.release.ReleaseWorkload.get_DependsOn",
            "jd.resolver.release.ReleaseWorkload.get_Image",
            "jd.resolver.release.ReleaseWorkload.get_Name",
            "jd.resolver.release.ReleaseWorkload.get_Team",
        ],
        ["jd.cli.ReleaseDeployCli"] =
        [
            "jd.core.records.IRecordStore.ReadAsync",
            "jd.orchestrator.IqCheck.get_Failure",
            "jd.orchestrator.IqCheck.get_Passed",
            "jd.orchestrator.IqCheck.get_Workload",
            "jd.orchestrator.IqResult.get_Checks",
            "jd.orchestrator.IqResult.get_Passed",
            "jd.orchestrator.Orchestrator..ctor",
            "jd.orchestrator.Orchestrator.DeployReleaseAsync",
            "jd.orchestrator.Qualifier..ctor",
            "jd.orchestrator.Qualifier.QualifyAsync",
            "jd.orchestrator.ReleaseRecordBuilder.Build",
            "jd.orchestrator.ReleaseRecordBuilder.BuildQualify",
            "jd.orchestrator.WorkloadRun.get_Run",
            "jd.orchestrator.WorkloadRun.get_Workload",
            "jd.orchestrator.usecases.Checks.EnvironmentType",
            "jd.orchestrator.usecases.Checks.Gate",
            "jd.orchestrator.usecases.Checks.Templates",
            "jd.orchestrator.usecases.Connections.get_Backend",
            "jd.orchestrator.usecases.Connections.get_LiveRevisions",
            "jd.orchestrator.usecases.Connections.get_Probe",
            "jd.orchestrator.usecases.Connections.get_Records",
            "jd.orchestrator.usecases.Connections.get_Time",
            "jd.resolver.LoadResult`1.get_Errors",
            "jd.resolver.LoadResult`1.get_Value",
            "jd.resolver.catalog.CatalogParser.ParseAsync",
            "jd.resolver.environment.EnvironmentDescriptor.get_Name",
            "jd.resolver.environment.EnvironmentParser.LoadAsync",
            "jd.resolver.release.ReleaseHash.Of",
            "jd.resolver.release.ReleaseRecordFile.KeepSetAsync",
            "jd.resolver.release.ReleaseRecordFile.KeptSetName",
            "jd.resolver.release.ReleaseRecordFile.WriteAsync",
            "jd.resolver.release.ReleaseResolver.ResolveAsync",
            "jd.resolver.release.ReleaseSet.get_Label",
            "jd.resolver.release.ReleaseSet.get_Order",
            "jd.resolver.release.ReleaseSetFile.LoadAsync",
            "jd.resolver.release.ReleaseSetFile.ParseAsync",
            "jd.resolver.release.ReleaseSetResult.get_Bytes",
            "jd.resolver.release.ReleaseSetResult.get_Errors",
            "jd.resolver.release.ReleaseSetResult.get_Set",
        ],
    };

    // R2 today: the commands that build the orchestrator, the qualifier, the records and the templates from the factories, so name the ports they return.
    private static readonly Dictionary<string, string[]> R2Allowlist = new()
    {
        ["jd.cli.ApproveCli"] =
        [
            "jd.core.records.IRecordStore",
        ],
        ["jd.cli.Cli"] =
        [
            "jd.core.bp.IBackEndProvider",
            "jd.core.identity.IDeployerIdentity",
            "jd.orchestrator.IRevisionProbe",
            "jd.orchestrator.ITemplates",
        ],
        ["jd.cli.PromoteCli"] =
        [
            "jd.core.records.IRecordStore",
        ],
        ["jd.cli.ReleaseDeployCli"] =
        [
            "jd.core.bp.IBackEndProvider",
            "jd.core.records.IRecordStore",
            "jd.core.revisions.ILiveRevisions",
            "jd.orchestrator.IRevisionProbe",
            "jd.orchestrator.ITemplates",
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
        ["jd.resolver.catalog.CatalogDirectory"] =
        [
            "System.IO.Directory.EnumerateFiles",
            "System.IO.Directory.Exists",
            "System.IO.File.ReadAllBytesAsync",
        ],
        ["jd.resolver.release.ReleaseBuilder"] =
        [
            "System.IO.File.Exists",
            "System.IO.File.ReadAllBytesAsync",
            "System.IO.Path.GetFullPath",
        ],
        ["jd.resolver.release.ReleaseSetFile"] =
        [
            "System.IO.File.Exists",
            "System.IO.File.ReadAllBytesAsync",
            "System.IO.FileStream..ctor",
        ],
    };
}
