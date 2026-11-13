using jd.core.identity;
using jd.core.revisions;
using jd.orchestrator.usecases;
using jd.resolver.release;
using jd.resolver.tests;
using Xunit;

namespace jd.orchestrator.tests;

// The Promote use case over the Ledger's records (the use-case tests' set kept on dev, deployed there by Carol) and a gated `stage` descriptor: the order
// of its checks, the refusal each returns, and that a refusal deploys nothing, keeps no set on stage and builds no backend (approvals.md section 5).
public partial class OrchestratorTests
{
    private static readonly string KeptOnDev = Ledger.Key(ReleaseRecordFile.SetFileName);
    private static readonly string KeptOnStage = MemoryRecordStore.Key("stage", "1.0.0", ReleaseRecordFile.SetFileName);

    // stage's descriptor over the use-case tests' dev, gated by a policy over dev that lists Alice, Bob and Carol.
    private static readonly string GatedStage =
        WorkloadUseCaseTests.Environment.Replace("name: dev", "name: stage", StringComparison.Ordinal).Replace("type: dev", "type: stage", StringComparison.Ordinal)
        + $"approval:\n  qualifiedOn: dev\n  required: 1\n  approvers: [\"{Alice}\", \"{Bob}\", \"{Carol}\"]\n  separateFromDeployer: true\n";

    // A promotion run over a ledger: the factories it invoked in order, the progress it reported, and the backend the walk reaches.
    private sealed class PromoteRun(Ledger ledger, IDeployerIdentity identity)
    {
        public Ledger Ledger { get; } = ledger;

        public List<string> Built { get; } = [];

        public List<Progress> Progress { get; } = [];

        // A deploy reports a change, and a preview of what it deployed none, so IQ passes.
        public FakeBackend Backend { get; } = new() { DetectChanges = true, Summary = new() { ["Same"] = 1 } };

        // What stage's directory holds besides its approvals.
        public IEnumerable<string> Kept =>
            Ledger.Records.Files.Keys.Where(key => key.StartsWith(StagePrefix, StringComparison.Ordinal) && !key.EndsWith("-approve.json", StringComparison.Ordinal));

        // during is told each progress as it is reported, after it is collected.
        public Task<PromoteResult> RunAsync(string? target = null, string label = "1.0.0", Action<Progress>? during = null) =>
            Promote.RunAsync(
                new PromoteInput(label, WorkloadUseCaseTests.Document("stage.yaml", target ?? GatedStage), WorkloadUseCaseTests.Catalog),
                Connections(),
                progress =>
                {
                    Progress.Add(progress);
                    during?.Invoke(progress);
                },
                CancellationToken.None);

        private Connections Connections() => new(
            () => Build("records", Ledger.Records),
            () => Build("identity", identity),
            () => Build<ILiveRevisions>("live revisions", new FakeLiveRevisions(_ => [new LiveRevision("app--1", 100, [ReleaseUseCaseRunTests.Image])])),
            () => Build("backend", Backend),
            () => new FakeProbe(Backend.Log),
            () => TimeProvider.System,
            part => new RecordingConnections(Backend).With().Templates(part));

        private T Build<T>(string factory, T built)
        {
            Built.Add(factory);
            return built;
        }
    }

    // The set kept on dev for 1.0.0, deployed by Carol with the IQ given (passed by default) and approved for stage by Alice; Dave runs the promotion,
    // neither an approver nor the deployer.
    private static async Task<PromoteRun> PromoteRunAsync(IqResult? iq = null, bool approved = true, Exception? identityFailure = null)
    {
        var ledger = await Ledger.CreateAsync(await ReleaseStartTests.SetAsync());
        await ledger.DeployAsync(1, iq ?? PassingIq, objectId: Carol);
        if (approved)
        {
            await ApproveAsync(ledger, Alice, 2);
        }

        return new PromoteRun(ledger, new FakeIdentity(Dave, identityFailure));
    }

    // A refusal before the walk: no backend built or called, nothing kept on stage.
    private static void AssertNothingDeployed(PromoteRun run, PromoteResult result)
    {
        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal((null, null, null), (result.Release, result.Environment, result.SetSha256));
        Assert.DoesNotContain("backend", run.Built);
        Assert.Empty(run.Backend.Calls);
        Assert.Empty(run.Kept);
    }

    [Fact]
    public async Task A_qualified_and_approved_label_is_promoted_by_the_walk_of_its_kept_bytes_after_promoting_is_reported()
    {
        var run = await PromoteRunAsync();

        var result = await run.RunAsync();

        Assert.Equal((Outcome.Succeeded, (Problem?)null), (result.Outcome, result.Problem));
        Assert.Empty(result.Errors);
        Assert.Equal(("stage", run.Ledger.SetSha256), (result.Environment?.Name, result.SetSha256));
        var release = Assert.IsType<DeployReleaseResult>(result.Release);
        Assert.Equal((Outcome.Succeeded, "1.0.0", "stage", true), (release.Outcome, release.Set?.Label, release.Environment?.Name, release.Iq?.Passed));
        Assert.Equal<Progress>([new Promoting("1.0.0", "dev", run.Ledger.SetSha256), new WorkloadStarted("shop")], run.Progress.Take(2));
        Assert.All(run.Progress.Skip(2), progress => Assert.IsType<NodeFinished>(progress));
        Assert.Equal(["identity", "records", "records", "backend", "live revisions"], run.Built);
        Assert.Equal(run.Ledger.Records.Files[KeptOnDev], run.Ledger.Records.Files[KeptOnStage]);
        Assert.Contains(run.Backend.Log, line => line.StartsWith("deploy shop.stage.", StringComparison.Ordinal));
    }

    // Security boundary: the label is refused before the descriptor is parsed or anything outside jd is built.
    [Theory]
    [InlineData("foo", "'foo' is not a release label (like 1.0.0 or 1.0.0-rc.1).")]
    [InlineData("../1.0.0", "'../1.0.0' cannot name a directory of the release records: it is empty or contains '..', '/' or '\\'.")]
    public async Task A_label_that_is_refused_is_invalid_input_before_the_promotion_target_is_parsed(string label, string reason)
    {
        var run = await PromoteRunAsync();

        var result = await run.RunAsync("not: [a descriptor", label);

        Assert.Equal((Outcome.InvalidInput, (Problem?)new LabelInvalid(reason)), (result.Outcome, result.Problem));
        Assert.Empty(result.Errors);
        Assert.Empty(run.Built);
        Assert.Empty(run.Progress);
    }

    [Fact]
    public async Task A_promotion_target_with_errors_fails_with_them_before_the_identity()
    {
        var run = await PromoteRunAsync();

        var result = await run.RunAsync("kind: Environment\n");

        Assert.Null(result.Problem);
        Assert.NotEmpty(result.Errors);
        Assert.All(result.Errors, error => Assert.Equal("stage.yaml", error.File));
        Assert.Empty(run.Built);
        AssertNothingDeployed(run, result);
    }

    [Fact]
    public async Task An_ungated_environment_is_refused_before_the_identity()
    {
        var run = await PromoteRunAsync();

        var result = await run.RunAsync(WorkloadUseCaseTests.Environment);

        Assert.Equal<Problem?>(new NotGated("dev"), result.Problem);
        Assert.Empty(run.Built);
        AssertNothingDeployed(run, result);
    }

    // Security boundary: the identity is checked before anything is read or recorded.
    [Fact]
    public async Task A_refused_identity_stops_before_the_records_are_read_or_anything_is_kept()
    {
        var run = await PromoteRunAsync(identityFailure: new InvalidOperationException("ARM_TENANT_ID is not set."));

        var result = await run.RunAsync();

        Assert.Equal<Problem?>(new IdentityRefused("ARM_TENANT_ID is not set."), result.Problem);
        Assert.Equal(["identity"], run.Built);
        AssertNothingDeployed(run, result);
    }

    // Security boundary: a promotion without the required approvals deploys nothing and builds no backend.
    [Fact]
    public async Task A_label_without_the_required_approvals_is_not_promotable_and_nothing_is_deployed()
    {
        var run = await PromoteRunAsync(approved: false);

        var result = await run.RunAsync();

        var refusal = Assert.IsType<NotPromotable>(result.Problem);
        Assert.Equal(("1.0.0", "stage"), (refusal.Label, refusal.Environment));
        Assert.Equal(["0 of 1 required approvals for 'stage' count."], refusal.Reasons);
        Assert.Equal(["identity", "records"], run.Built);
        Assert.Empty(run.Progress);
        AssertNothingDeployed(run, result);
    }

    [Fact]
    public async Task A_label_that_is_not_qualified_is_not_promotable_however_it_is_approved()
    {
        var run = await PromoteRunAsync(FailingIq);

        var result = await run.RunAsync();

        var refusal = Assert.IsType<NotPromotable>(result.Problem);
        Assert.StartsWith("not qualified on dev: ", Assert.Single(refusal.Reasons), StringComparison.Ordinal);
        AssertNothingDeployed(run, result);
    }

    [Fact]
    public async Task An_unknown_label_is_not_promotable_with_its_reason()
    {
        var run = await PromoteRunAsync();

        var result = await run.RunAsync(label: "9.9.9");

        var refusal = Assert.IsType<NotPromotable>(result.Problem);
        Assert.Equal(["unknown label: there is no set.yaml for label 9.9.9 on dev."], refusal.Reasons);
        AssertNothingDeployed(run, result);
    }

    [Fact]
    public async Task Each_approval_file_that_cannot_count_is_reported_as_ignored_before_the_promotion_goes_ahead()
    {
        var run = await PromoteRunAsync();
        await ApproveAsync(run.Ledger, Bob, 3, new string('0', 64));

        var result = await run.RunAsync();

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        var ignored = Assert.IsType<Ignored>(run.Progress[0]);
        Assert.StartsWith("20261017T", ignored.What, StringComparison.Ordinal);
        Assert.IsType<Promoting>(run.Progress[1]);
    }

    // Security boundary: the bytes deployed are the bytes judged; a kept set changed after the check is refused before it is resolved (it does not
    // resolve, and no resolution error comes back), so nothing is deployed or kept on stage.
    [Fact]
    public async Task A_kept_set_whose_bytes_changed_after_the_check_is_refused_before_it_is_resolved()
    {
        var run = await PromoteRunAsync();
        var changed = await ReleaseStartTests.SetAsync("no-such-type");

        var result = await run.RunAsync(during: progress =>
        {
            if (progress is Promoting)
            {
                run.Ledger.Records.Files[KeptOnDev] = changed;
            }
        });

        Assert.Equal(Outcome.Failed, result.Outcome);
        var release = Assert.IsType<DeployReleaseResult>(result.Release);
        Assert.Equal<Problem?>(new KeptSetChanged(KeptOnDev, run.Ledger.SetSha256), release.Problem);
        Assert.Empty(release.Errors);
        Assert.DoesNotContain("backend", run.Built);
        Assert.Empty(run.Backend.Calls);
        Assert.Empty(run.Kept);
    }

    // Security boundary: the set is kept on stage before the backend is built, and a conflict there deploys nothing.
    [Fact]
    public async Task Another_set_kept_on_the_target_under_the_label_is_a_recording_refusal_before_the_backend_is_built()
    {
        var run = await PromoteRunAsync();
        run.Ledger.Records.Put(KeptOnStage, "another set");

        var result = await run.RunAsync();

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.IsType<RecordingRefused>(result.Release?.Problem);
        Assert.DoesNotContain("backend", run.Built);
        Assert.Empty(run.Backend.Calls);
        Assert.Equal("another set", run.Ledger.Records.Text(KeptOnStage));
    }
}
