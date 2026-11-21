using jd.core.identity;
using jd.orchestrator.usecases;
using jd.resolver.release;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.orchestrator.tests;

// Records written before ADR 0022 carry the identity {tenantId, clientId, objectId}, which the record schemas keep as read, never written. A principal
// is one deployer and one approver whichever shape records it (docs/architecture/approvals.md section 5): records come from the real builders and are
// rewritten to the old shape, as the files written before the change are. The golden release record's legacy twin is written by hand.
public partial class OrchestratorTests
{
    // Rewrites a record's identity to the shape jd wrote before ADR 0022, keeping its principal.
    private static void InOldShape(JObject record)
    {
        var identity = record["identity"]!;
        record["identity"] = new JObject { ["tenantId"] = identity["directory"], ["clientId"] = identity["name"], ["objectId"] = identity["principal"] };
    }

    // An approval by the principal at the given minute, in the old identity shape, placed in stage's directory for the label.
    private static async Task ApproveInOldShapeAsync(Ledger ledger, string principal, int minute)
    {
        var at = RecordStart.AddMinutes(minute);
        var approval = JObject.Parse(ApprovalRecord.Build(Stage(), "1.0.0", ledger.SetSha256, new DeployerIdentity("tenant-1", principal, "client-1"), "ready", at));
        InOldShape(approval);
        await ReleaseRecordFile.WriteAsync(ledger.Records, "stage", "1.0.0", at, ReleaseRecordFile.ApproveActivity, approval.ToString());
    }

    // Security boundary: the legacy twin is valid and reads to the principal of the golden record, and differs from it in nothing else.
    [Fact]
    public async Task The_golden_release_record_and_its_legacy_twin_are_valid_and_read_to_the_same_principal()
    {
        var golden = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "release-record.json"));
        var legacy = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "release-record-legacy.json"));
        var (goldenRecord, legacyRecord) = (Parse(golden), Parse(legacy));

        await AssertValidAsync(golden);
        await AssertValidAsync(legacy);
        Assert.Equal(new DeployerIdentity("tenant-1", "object-1", "client-1"), RecordIdentity.Read(goldenRecord["identity"]!));
        Assert.Equal(new DeployerIdentity("tenant-1", "object-1", "client-1"), RecordIdentity.Read(legacyRecord["identity"]!));
        Assert.Equal(new JObject { ["tenantId"] = "tenant-1", ["clientId"] = "client-1", ["objectId"] = "object-1" }.ToString(), legacyRecord["identity"]!.ToString());
        goldenRecord.Remove("identity");
        legacyRecord.Remove("identity");
        Assert.Equal(goldenRecord.ToString(), legacyRecord.ToString());
    }

    [Fact]
    public async Task A_principal_that_deployed_in_both_shapes_is_one_deployer_of_a_qualified_label()
    {
        var ledger = await Ledger.CreateAsync();
        ledger.Forge(await ledger.DeployAsync(1, PassingIq, objectId: Carol), InOldShape);
        await ledger.DeployAsync(2, PassingIq, objectId: Carol.ToUpperInvariant());

        var result = await ledger.ReadAsync();

        Assert.True(result.Qualified, string.Join("; ", result.Reasons));
        Assert.Equal([Carol], result.Deployers.Select(d => d.ToLowerInvariant()));
    }

    // Security boundary: an identity is exactly one shape; a record with both, neither, or half of each fails the schema, so the label is not qualified.
    [Theory]
    [InlineData("both")]
    [InlineData("neither")]
    [InlineData("mixed")]
    public async Task A_release_record_whose_identity_has_both_shapes_or_neither_makes_the_label_unqualified(string shape)
    {
        var ledger = await Ledger.CreateAsync();
        var deploy = await ledger.DeployAsync(1, PassingIq);
        ledger.Forge(deploy, record => record["identity"] = shape switch
        {
            "both" => new JObject
            {
                ["directory"] = "tenant-1",
                ["principal"] = "object-1",
                ["name"] = "client-1",
                ["tenantId"] = "tenant-1",
                ["clientId"] = "client-1",
                ["objectId"] = "object-1",
            },
            "neither" => new JObject(),
            _ => new JObject { ["directory"] = "tenant-1", ["principal"] = "object-1", ["clientId"] = "client-1" },
        });

        var result = await ledger.ReadAsync();

        Assert.False(result.Qualified);
        Assert.StartsWith($"{Name(deploy)}: is not a valid release record: ", OneReason(result), StringComparison.Ordinal);
        Assert.Empty(result.Deployers);
    }

    // Security boundary: separation of duties holds across the shapes: the deployer recorded in the old shape is refused when jd approve names them in the new.
    [Fact]
    public async Task A_deployer_recorded_in_the_old_shape_cannot_approve_their_own_qualification()
    {
        var ledger = await Ledger.CreateAsync();
        ledger.Forge(await ledger.DeployAsync(1, PassingIq, objectId: Carol), InOldShape);
        var approving = new Approving(ledger, new FakeIdentity(Carol));

        var result = await approving.RunAsync(StageDescriptor(separate: true));

        Assert.Equal((Outcome.Failed, (Problem?)new NotSeparate(Carol, "1.0.0", "dev", "stage")), (result.Outcome, result.Problem));
        Assert.Empty(approving.Written);
    }

    // Security boundary: at promotion, a deployer's approval does not count with separation on, whichever of the two records is in the old shape.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_deployers_approval_does_not_count_with_separation_on_whichever_record_is_in_the_old_shape(bool deployInOldShape, bool approvalInOldShape)
    {
        var ledger = await Ledger.CreateAsync();
        var deploy = await ledger.DeployAsync(1, PassingIq, objectId: "object-1");
        if (deployInOldShape)
        {
            ledger.Forge(deploy, InOldShape);
        }

        await (approvalInOldShape ? ApproveInOldShapeAsync(ledger, "object-1", 2) : ApproveAsync(ledger, "object-1", 2));

        var decision = await CheckAsync(ledger, Stage(separate: true));

        Assert.Equal("0 of 1 required approvals for 'stage' count.", Assert.Single(decision.Reasons));
        Assert.Empty(decision.IgnoredApprovals);
    }

    // Security boundary: one principal approves a set once, whichever shape recorded the earlier approval.
    [Fact]
    public async Task A_principal_whose_approval_is_in_the_old_shape_is_refused_a_second_approval()
    {
        var ledger = await DeployedByCarolAsync(PassingIq);
        await ApproveInOldShapeAsync(ledger, Alice, 2);
        var approving = new Approving(ledger, new FakeIdentity(Alice));
        var before = approving.Written.ToList();

        var result = await approving.RunAsync(StageDescriptor());

        Assert.Equal((Outcome.Failed, (Problem?)new AlreadyApproved(Alice, "1.0.0", "stage")), (result.Outcome, result.Problem));
        Assert.Equal(before, approving.Written);
    }

    // Approvals in the old shape count, and a principal approving in both shapes counts once.
    [Fact]
    public async Task Approvals_count_across_the_shapes_and_one_principal_in_both_counts_once()
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        await ApproveInOldShapeAsync(ledger, Alice, 2);
        await ApproveAsync(ledger, Alice, 3);

        var once = await CheckAsync(ledger, Stage(required: 2));
        await ApproveInOldShapeAsync(ledger, Bob, 4);
        var twice = await CheckAsync(ledger, Stage(required: 2));

        Assert.Equal("1 of 2 required approvals for 'stage' count.", Assert.Single(once.Reasons));
        Assert.True(twice.MayPromote, string.Join("; ", twice.Reasons.Concat(twice.IgnoredApprovals)));
    }

    [Fact]
    public async Task The_count_of_jd_approve_includes_an_earlier_approval_in_the_old_shape()
    {
        var ledger = await DeployedByCarolAsync(PassingIq);
        await ApproveInOldShapeAsync(ledger, Bob, 2);
        var approving = new Approving(ledger, new FakeIdentity(Alice));

        var result = await approving.RunAsync(StageDescriptor(required: 2));

        Assert.Equal((Outcome.Succeeded, 2), (result.Outcome, result.Counted));
    }
}
