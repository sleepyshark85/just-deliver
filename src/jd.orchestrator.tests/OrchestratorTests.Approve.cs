using System.Text;
using jd.core.identity;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.tests;
using Xunit;

namespace jd.orchestrator.tests;

// The Approve use case over the Ledger's records (dev, deployed by Carol) and a gated `stage` descriptor: the order of its checks, the refusal each
// returns, and that a refusal writes nothing into stage's directory (docs/architecture/approvals.md sections 4 and 7).
public partial class OrchestratorTests
{
    private const string Carol = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string Dave = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    private const string StagePrefix = "stage/";

    // The time the clock of an approval reads: the approval is stage/1.0.0/20261017T090000000Z-approve.json.
    private static readonly DateTimeOffset ApprovedAt = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static readonly string ApprovalKey = MemoryRecordStore.Key("stage", "1.0.0", "20261017T090000000Z-approve.json");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // The principal of the credential, or the failure of a credential that cannot name one.
    private sealed class FakeIdentity(string objectId, Exception? failure = null) : IDeployerIdentity
    {
        public Task<DeployerIdentity> GetAsync(CancellationToken cancellationToken) =>
            failure is null ? Task.FromResult(new DeployerIdentity("tenant-1", "client-1", objectId)) : throw failure;
    }

    // An approval run: its records, the factories it invoked in order, and the progress it reported.
    private sealed class Approving(Ledger ledger, IDeployerIdentity identity)
    {
        public List<string> Built { get; } = [];

        public List<Progress> Progress { get; } = [];

        // The files written into stage's directory.
        public IEnumerable<string> Written => ledger.Records.Files.Keys.Where(key => key.StartsWith(StagePrefix, StringComparison.Ordinal));

        public Task<ApproveResult> RunAsync(string environment, string label = "1.0.0", string meaning = "ready") =>
            Approve.RunAsync(new ApproveInput(label, new SourceDocument("stage.yaml", Encoding.UTF8.GetBytes(environment)), meaning), Connections(), Progress.Add, CancellationToken.None);

        private Connections Connections() => new(
            () => Build("records", ledger.Records),
            () => Build("identity", identity),
            () => throw new NotSupportedException(),
            () => throw new NotSupportedException(),
            () => throw new NotSupportedException(),
            () => Build<TimeProvider>("time", new FixedClock(ApprovedAt)),
            _ => throw new NotSupportedException());

        private T Build<T>(string factory, T built)
        {
            Built.Add(factory);
            return built;
        }
    }

    // stage's descriptor, gated by a policy over dev that lists Alice, Bob and Carol.
    private static string StageDescriptor(int required = 1, bool separate = true) =>
        "kind: Environment\nname: stage\nregion: region-1\ntype: stage\noffers: [thing]\nvalues: {}\napproval:\n  qualifiedOn: dev\n"
        + $"  required: {required}\n  approvers: [\"{Alice}\", \"{Bob}\", \"{Carol}\"]\n  separateFromDeployer: {(separate ? "true" : "false")}\n";

    // dev's records of 1.0.0, deployed by Carol with the given IQ.
    private static async Task<Ledger> DeployedByCarolAsync(IqResult iq)
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, iq, objectId: Carol);
        return ledger;
    }

    private static async Task<Approving> ApprovingAsync(string objectId, IqResult? iq = null) => new(await DeployedByCarolAsync(iq ?? PassingIq), new FakeIdentity(objectId));

    [Fact]
    public async Task An_approval_by_a_listed_principal_of_a_qualified_set_writes_one_record_and_counts_it()
    {
        var approving = await ApprovingAsync(Alice);

        var result = await approving.RunAsync(StageDescriptor(), meaning: new string('m', 500));

        Assert.Equal((Outcome.Succeeded, (Problem?)null), (result.Outcome, result.Problem));
        Assert.Empty(result.Errors);
        Assert.Equal(("stage", Alice, 1, ApprovalKey), (result.Environment?.Name, result.Approver?.ObjectId, result.Counted, result.Record));
        Assert.Equal([ApprovalKey], approving.Written);
        Assert.Equal(["identity", "records", "time"], approving.Built);
        Assert.Empty(approving.Progress);
    }

    // Security boundary: the label and the meaning are refused before the descriptor is parsed or anything outside jd is built.
    [Theory]
    [InlineData("foo", "ready", "'foo' is not a release label (like 1.0.0 or 1.0.0-rc.1).")]
    [InlineData("../1.0.0", "ready", "'../1.0.0' cannot name a directory of the release records: it is empty or contains '..', '/' or '\\'.")]
    [InlineData("foo", "", "'foo' is not a release label (like 1.0.0 or 1.0.0-rc.1).")]
    [InlineData("1.0.0", "", null)]
    [InlineData("1.0.0", "a\nb", null)]
    [InlineData("1.0.0", "a\u0085b", null)]
    public async Task A_label_or_meaning_that_is_refused_is_invalid_input_before_the_descriptor_is_parsed(string label, string meaning, string? labelReason)
    {
        var approving = await ApprovingAsync(Alice);

        var result = await approving.RunAsync("not: [a descriptor", label, meaning);

        Assert.Equal(Outcome.InvalidInput, result.Outcome);
        Assert.Equal(labelReason is null ? new MeaningInvalid(500) : new LabelInvalid(labelReason), result.Problem);
        Assert.Empty(result.Errors);
        Assert.Empty(approving.Built);
        Assert.Empty(approving.Written);
    }

    [Fact]
    public async Task A_meaning_of_501_characters_is_invalid_input()
    {
        var approving = await ApprovingAsync(Alice);

        var result = await approving.RunAsync(StageDescriptor(), meaning: new string('m', 501));

        Assert.Equal((Outcome.InvalidInput, (Problem?)new MeaningInvalid(500)), (result.Outcome, result.Problem));
        Assert.Empty(approving.Written);
    }

    [Fact]
    public async Task A_descriptor_with_errors_fails_with_them_before_the_identity()
    {
        var approving = await ApprovingAsync(Alice);

        var result = await approving.RunAsync("kind: Environment\n");

        Assert.Equal((Outcome.Failed, (Problem?)null), (result.Outcome, result.Problem));
        Assert.Equal("stage.yaml: name: schema violation: PropertyRequired: #/name", result.Errors[0].ToString());
        Assert.Empty(approving.Built);
    }

    // Security boundary: an environment without an approval policy has nothing to approve.
    [Fact]
    public async Task An_ungated_environment_is_refused_before_the_identity_and_writes_nothing()
    {
        var approving = await ApprovingAsync(Alice);

        var result = await approving.RunAsync(StageDescriptor()[..StageDescriptor().IndexOf("approval:", StringComparison.Ordinal)]);

        Assert.Equal((Outcome.Failed, (Problem?)new NotGated("stage")), (result.Outcome, result.Problem));
        Assert.Empty(approving.Built);
        Assert.Empty(approving.Written);
    }

    // Security boundary: a credential that cannot name a principal stops the approval before any record is read.
    [Fact]
    public async Task A_refused_identity_stops_before_the_records_are_read_and_writes_nothing()
    {
        var approving = new Approving(await DeployedByCarolAsync(PassingIq), new FakeIdentity(Alice, new InvalidOperationException("ARM_TENANT_ID is not set.")));

        var result = await approving.RunAsync(StageDescriptor());

        Assert.Equal((Outcome.Failed, (Problem?)new IdentityRefused("ARM_TENANT_ID is not set.")), (result.Outcome, result.Problem));
        Assert.Equal(["identity"], approving.Built);
        Assert.Empty(approving.Written);
    }

    // Security boundary: a label with no kept set on the policy's environment is refused.
    [Fact]
    public async Task An_unknown_label_is_refused_and_writes_nothing()
    {
        var approving = await ApprovingAsync(Alice);

        var result = await approving.RunAsync(StageDescriptor(), label: "2.0.0");

        var unknown = Assert.IsType<UnknownLabel>(result.Problem);
        Assert.Equal("2.0.0", unknown.Label);
        Assert.Equal(["unknown label: there is no set.yaml for label 2.0.0 on dev."], unknown.Reasons);
        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Empty(approving.Written);
    }

    // Security boundary: a set not qualified on the policy's environment is refused, with every reason.
    [Fact]
    public async Task A_label_not_qualified_on_the_policys_environment_is_refused_with_every_reason_and_writes_nothing()
    {
        var approving = await ApprovingAsync(Alice, FailingIq);

        var result = await approving.RunAsync(StageDescriptor());

        var notQualified = Assert.IsType<NotQualified>(result.Problem);
        Assert.Equal(("1.0.0", "dev"), (notQualified.Label, notQualified.QualifiedOn));
        Assert.Equal(["IQ of the newest record (20261017T080100000Z-deploy.json) is failed."], notQualified.Reasons);
        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Empty(approving.Written);
    }

    // Security boundary: only a listed principal may approve.
    [Fact]
    public async Task A_principal_not_listed_in_the_approvers_is_refused_and_writes_nothing()
    {
        var approving = await ApprovingAsync(Dave);

        var result = await approving.RunAsync(StageDescriptor());

        Assert.Equal((Outcome.Failed, (Problem?)new NotApprover(Dave, "stage")), (result.Outcome, result.Problem));
        Assert.Empty(approving.Written);
    }

    // Security boundary: the deployer may not approve their own set when the policy separates them, and may when it does not.
    [Fact]
    public async Task The_deployer_is_refused_when_the_policy_separates_and_accepted_when_it_does_not()
    {
        var approving = await ApprovingAsync(Carol);

        var strict = await approving.RunAsync(StageDescriptor(separate: true));

        Assert.Equal((Outcome.Failed, (Problem?)new NotSeparate(Carol, "1.0.0", "dev", "stage")), (strict.Outcome, strict.Problem));
        Assert.Empty(approving.Written);
        Assert.Equal(Outcome.Succeeded, (await approving.RunAsync(StageDescriptor(separate: false))).Outcome);
        Assert.Equal([ApprovalKey], approving.Written);
    }

    // Security boundary: one principal approves a set once.
    [Fact]
    public async Task A_second_approval_by_the_same_principal_is_refused_whatever_the_case_of_its_id_and_writes_nothing()
    {
        var ledger = await DeployedByCarolAsync(PassingIq);
        await ApproveAsync(ledger, Alice.ToUpperInvariant(), 2);
        var approving = new Approving(ledger, new FakeIdentity(Alice));
        var before = approving.Written.ToList();

        var result = await approving.RunAsync(StageDescriptor());

        Assert.Equal((Outcome.Failed, (Problem?)new AlreadyApproved(Alice, "1.0.0", "stage")), (result.Outcome, result.Problem));
        Assert.Equal(before, approving.Written);
    }

    // Security boundary: a record that cannot be written is a refusal, and the file in the way keeps its bytes.
    [Fact]
    public async Task A_record_that_cannot_be_written_is_record_not_written_and_the_existing_file_stays()
    {
        var ledger = await DeployedByCarolAsync(PassingIq);
        ledger.Records.Put(ApprovalKey, "old bytes");
        var approving = new Approving(ledger, new FakeIdentity(Alice));

        var result = await approving.RunAsync(StageDescriptor());

        Assert.Equal((Outcome.Failed, (Problem?)new RecordNotWritten($"The file '{ApprovalKey}' already exists.")), (result.Outcome, result.Problem));
        Assert.Equal("old bytes", ledger.Records.Text(ApprovalKey));
    }

    [Fact]
    public async Task Each_approval_file_that_cannot_count_is_reported_as_ignored_and_the_approval_goes_ahead()
    {
        var ledger = await DeployedByCarolAsync(PassingIq);
        ledger.Records.Put(MemoryRecordStore.Key("stage", "1.0.0", "notes-approve.json"), "{}");
        var approving = new Approving(ledger, new FakeIdentity(Alice));

        var result = await approving.RunAsync(StageDescriptor());

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Equal([new Ignored("notes-approve.json: is not named <time>-approve.json with a time like 20261017T030405123Z.")], approving.Progress);
    }

    // The count is of the distinct earlier approvals that count for the policy (listed, and not by the deployer when separate), and this one.
    [Fact]
    public async Task The_count_is_the_earlier_approvals_that_count_and_this_one()
    {
        var ledger = await DeployedByCarolAsync(PassingIq);
        await ApproveAsync(ledger, Bob, 2);
        await ApproveAsync(ledger, Carol, 3); // the deployer
        await ApproveAsync(ledger, Dave, 4); // not listed
        var approving = new Approving(ledger, new FakeIdentity(Alice));

        var result = await approving.RunAsync(StageDescriptor(required: 2));

        Assert.Equal((Outcome.Succeeded, 2), (result.Outcome, result.Counted));
    }
}
