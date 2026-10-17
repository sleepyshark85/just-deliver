using jd.core.identity;
using jd.resolver.environment;
using jd.resolver.release;
using Xunit;

namespace jd.orchestrator.tests;

// May a label be promoted (docs/architecture/approvals.md section 5, steps 1 to 4): dev's records come from the real record builder through the real store (the Ledger),
// the approvals from the real ApprovalRecord builder. Forgery is placing or editing files, which is all someone with the records directory can do.
public partial class OrchestratorTests
{
    private const string Alice = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Bob = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    // stage, gated by a policy over dev. The default deployer of the Ledger ("object-1") is listed so a test can make the deployer approve.
    private static EnvironmentDescriptor Stage(int required = 1, bool separate = true) =>
        new("stage", "region-1", "protected", new Dictionary<string, string>(), [], new ApprovalPolicy("dev", required, [Alice, Bob, "object-1"], separate));

    // An approval by objectId at the given minute, written through the store into stage's directory for the label, as `jd approve` would.
    private static async Task ApproveAsync(Ledger ledger, string objectId, int minute, string? setSha256 = null)
    {
        var at = RecordStart.AddMinutes(minute);
        var json = ApprovalRecord.Build(Stage(), "1.0.0", setSha256 ?? ledger.SetSha256, new DeployerIdentity("tenant-1", "client-1", objectId), "ready", at);
        await ReleaseRecordFile.WriteAsync(ledger.Records, "stage", "1.0.0", at, ReleaseRecordFile.ApproveActivity, json);
    }

    private static Task<PromotionDecision> CheckAsync(Ledger ledger, EnvironmentDescriptor target) => PromotionCheck.CheckAsync(ledger.Records, target, "1.0.0");

    [Fact]
    public async Task A_qualified_label_with_enough_approvals_may_be_promoted()
    {
        using var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        await ApproveAsync(ledger, Alice, 2);
        await ApproveAsync(ledger, Bob, 3);

        var decision = await CheckAsync(ledger, Stage(required: 2));

        Assert.True(decision.MayPromote, string.Join("; ", decision.Reasons));
        Assert.Equal(ledger.SetSha256, decision.SetSha256);
        Assert.Empty(decision.IgnoredApprovals);
    }

    [Fact]
    public async Task A_qualified_label_with_too_few_approvals_may_not_be_promoted_and_the_reason_counts_them()
    {
        using var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        await ApproveAsync(ledger, Alice, 2);

        var decision = await CheckAsync(ledger, Stage(required: 2));

        Assert.False(decision.MayPromote);
        Assert.Equal("1 of 2 required approvals for 'stage' count.", Assert.Single(decision.Reasons));
        Assert.Equal(ledger.SetSha256, decision.SetSha256);
    }

    [Fact]
    public async Task A_label_nobody_approved_may_not_be_promoted()
    {
        using var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);

        var decision = await CheckAsync(ledger, Stage());

        Assert.Equal("0 of 1 required approvals for 'stage' count.", Assert.Single(decision.Reasons));
    }

    [Fact]
    public async Task One_principal_approving_twice_counts_once()
    {
        using var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        await ApproveAsync(ledger, Alice, 2);
        await ApproveAsync(ledger, Alice.ToUpperInvariant(), 3);

        var decision = await CheckAsync(ledger, Stage(required: 2));

        Assert.Equal("1 of 2 required approvals for 'stage' count.", Assert.Single(decision.Reasons));
    }

    [Fact]
    public async Task A_deployers_approval_does_not_count_with_separation_on_and_does_with_it_off()
    {
        using var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq, objectId: "object-1");
        await ApproveAsync(ledger, "object-1", 2);

        var separate = await CheckAsync(ledger, Stage(separate: true));
        var together = await CheckAsync(ledger, Stage(separate: false));

        Assert.Equal("0 of 1 required approvals for 'stage' count.", Assert.Single(separate.Reasons));
        Assert.True(together.MayPromote, string.Join("; ", together.Reasons));
    }

    [Fact]
    public async Task An_approval_by_an_unlisted_principal_does_not_count()
    {
        using var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        await ApproveAsync(ledger, "dddddddd-dddd-dddd-dddd-dddddddddddd", 2);

        var decision = await CheckAsync(ledger, Stage());

        Assert.Equal("0 of 1 required approvals for 'stage' count.", Assert.Single(decision.Reasons));
        Assert.Empty(decision.IgnoredApprovals); // a valid approval that does not count is not an ignored one
    }

    [Fact]
    public async Task A_label_that_is_not_qualified_may_not_be_promoted_however_many_approve_and_the_reasons_are_listed()
    {
        using var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, FailingIq);
        await ApproveAsync(ledger, Alice, 2);

        var decision = await CheckAsync(ledger, Stage());

        Assert.False(decision.MayPromote);
        Assert.Equal(ledger.SetSha256, decision.SetSha256);
        Assert.Equal(["not qualified on dev: IQ of the newest record (20261017T080100000Z-deploy.json) is failed."], decision.Reasons);
    }

    [Fact]
    public async Task Not_qualified_and_not_approved_are_both_reasons()
    {
        using var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, FailingIq);

        var decision = await CheckAsync(ledger, Stage());

        Assert.Equal(2, decision.Reasons.Count);
        Assert.StartsWith("not qualified on dev: ", decision.Reasons[0], StringComparison.Ordinal);
        Assert.Equal("0 of 1 required approvals for 'stage' count.", decision.Reasons[1]);
    }

    [Fact]
    public async Task An_unknown_label_may_not_be_promoted_and_has_no_set()
    {
        using var ledger = await Ledger.CreateAsync();

        var decision = await PromotionCheck.CheckAsync(ledger.Records, Stage(), "9.9.9");

        Assert.False(decision.MayPromote);
        Assert.Null(decision.SetSha256);
        Assert.StartsWith("unknown label: ", Assert.Single(decision.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_set_yaml_edited_after_the_approval_withdraws_it_and_the_qualification()
    {
        using var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        await ApproveAsync(ledger, Alice, 2);
        await File.WriteAllTextAsync(Path.Combine(ledger.LabelDirectory, "set.yaml"), "another set");

        var decision = await CheckAsync(ledger, Stage());

        Assert.False(decision.MayPromote);
        Assert.NotEqual(ledger.SetSha256, decision.SetSha256);
        Assert.Equal(2, decision.Reasons.Count);
        Assert.Contains("pins set", decision.Reasons[0], StringComparison.Ordinal);
        Assert.Equal("0 of 1 required approvals for 'stage' count.", decision.Reasons[1]);
        Assert.Contains("approves set '", Assert.Single(decision.IgnoredApprovals), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ignored_approval_is_reported_but_does_not_stop_a_promotion_that_is_otherwise_allowed()
    {
        using var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        await ApproveAsync(ledger, Alice, 2);
        await ApproveAsync(ledger, Bob, 3, setSha256: new string('f', 64)); // approves another set

        var decision = await CheckAsync(ledger, Stage());

        Assert.True(decision.MayPromote, string.Join("; ", decision.Reasons));
        Assert.Contains("approves set '", Assert.Single(decision.IgnoredApprovals), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_environment_without_a_policy_cannot_be_promoted_to()
    {
        using var ledger = await Ledger.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => CheckAsync(ledger, Stage() with { Approval = null }));
    }
}
