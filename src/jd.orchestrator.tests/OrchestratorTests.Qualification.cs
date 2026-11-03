using jd.core.identity;
using jd.resolver.graph;
using jd.resolver.release;
using jd.resolver.tests;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.orchestrator.tests;

// The reader of a label's records (docs/architecture/approvals.md section 5, step 2). Records are built by the real ReleaseRecordBuilder and
// written through the record store's fake; forgery is editing, copying or renaming the written files, which is all someone with the records can do.
public partial class OrchestratorTests
{
    private static readonly IqResult FailingIq = new([new IqCheck(IqCheckKind.Drift, "shop", "shop/dev/thing/app", "node differs")], TimeSpan.FromSeconds(1));

    private sealed class Ledger
    {
        private readonly ReleaseSet _set = SetOf("shop", "cart");
        private readonly IReadOnlyList<ResolvedGraph> _graphs;
        private readonly IReadOnlyList<WorkloadRun> _passed;
        private readonly IReadOnlyList<WorkloadRun> _failed;

        private Ledger(MemoryRecordStore records, string setSha256, IReadOnlyList<ResolvedGraph> graphs, IReadOnlyList<WorkloadRun> passed, IReadOnlyList<WorkloadRun> failed)
        {
            (Records, SetSha256, _graphs, _passed, _failed) = (records, setSha256, graphs, passed, failed);
        }

        public MemoryRecordStore Records { get; }

        public string SetSha256 { get; }

        // The key of the label's file name in the records.
        public static string Key(string name) => MemoryRecordStore.Key("dev", "1.0.0", name);

        // A ledger whose label has its set.yaml, ready for records.
        public static async Task<Ledger> CreateAsync()
        {
            var (_, graphs, passed) = await ReleaseRunAsync(new FakeBackend(), null, "shop", "cart");
            var (_, _, failed) = await ReleaseRunAsync(new FakeBackend { FailOn = "shop.dev.thing.group" }, null, "shop", "cart");
            var records = new MemoryRecordStore();
            var (sha256, _) = await ReleaseRecordFile.KeepSetAsync(records, "dev", "1.0.0", "the set"u8.ToArray());
            return new Ledger(records, sha256, graphs, passed, failed);
        }

        // A deploy that finished at minute n of the hour, by the principal with the given object id; iq null is "did not run".
        public async Task<string> DeployAsync(int minute, IqResult? iq, string objectId = "object-1", bool failed = false)
        {
            var finished = RecordStart.AddMinutes(minute);
            var identity = new DeployerIdentity("tenant-1", "client-1", objectId);
            var json = ReleaseRecordBuilder.Build(_set, SetSha256, Env, _graphs, failed ? _failed : _passed, identity, finished.AddSeconds(-90), finished, TimeSpan.FromSeconds(42.5), iq);
            return await ReleaseRecordFile.WriteAsync(Records, "dev", "1.0.0", finished, ReleaseRecordFile.DeployActivity, json);
        }

        public async Task<string> QualifyAsync(int minute, IqResult iq)
        {
            var finished = RecordStart.AddMinutes(minute);
            var json = ReleaseRecordBuilder.BuildQualify(_set, SetSha256, Env, _graphs, Principal, finished.AddSeconds(-90), finished, iq);
            return await ReleaseRecordFile.WriteAsync(Records, "dev", "1.0.0", finished, ReleaseRecordFile.QualifyActivity, json);
        }

        public Task<Qualification> ReadAsync() => QualificationReader.ReadAsync(Records, "dev", "1.0.0");

        // Edits a written file in place, as someone with the records could.
        public void Forge(string key, Action<JObject> edit)
        {
            using var reader = new JsonTextReader(new StringReader(Records.Text(key))) { DateParseHandling = DateParseHandling.None };
            var body = JObject.Load(reader);
            edit(body);
            Records.Put(key, body.ToString(Formatting.Indented));
        }
    }

    private static string Name(string path) => Path.GetFileName(path);

    private static string OneReason(Qualification result) => Assert.Single(result.Reasons);

    [Fact]
    public async Task A_label_with_a_succeeded_deploy_whose_oq_and_iq_passed_is_qualified_and_names_its_set_and_deployer()
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);

        var result = await ledger.ReadAsync();

        Assert.True(result.Qualified, string.Join("; ", result.Reasons));
        Assert.Equal(ledger.SetSha256, result.SetSha256);
        Assert.Equal(["object-1"], result.Deployers);
    }

    [Fact]
    public async Task A_label_without_set_yaml_is_unknown_and_a_label_without_records_is_not_qualified()
    {
        var ledger = await Ledger.CreateAsync();

        var empty = await ledger.ReadAsync();
        var unknown = await QualificationReader.ReadAsync(ledger.Records, "dev", "9.9.9");

        Assert.Contains("never deployed", OneReason(empty), StringComparison.Ordinal);
        Assert.Equal(ledger.SetSha256, empty.SetSha256);
        Assert.Null(unknown.SetSha256);
        Assert.Contains("unknown label", OneReason(unknown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_that_says_another_label_another_environment_or_another_set_makes_the_label_unqualified()
    {
        var ledger = await Ledger.CreateAsync();
        var otherLabel = await ledger.DeployAsync(1, PassingIq);
        var otherEnvironment = await ledger.DeployAsync(2, PassingIq);
        var otherSet = await ledger.DeployAsync(3, PassingIq);
        ledger.Forge(otherLabel, r => r["release"]!["label"] = "2.0.0");
        ledger.Forge(otherEnvironment, r => r["environment"]!["name"] = "stage");
        ledger.Forge(otherSet, r => r["release"]!["setSha256"] = new string('f', 64));

        var result = await ledger.ReadAsync();

        Assert.False(result.Qualified);
        Assert.Equal(
            [
                $"{Name(otherLabel)}: is a record of label '2.0.0', not '1.0.0'.",
                $"{Name(otherEnvironment)}: is a record of environment 'stage', not 'dev'.",
                $"{Name(otherSet)}: pins set {new string('f', 64)}, but set.yaml hashes to {ledger.SetSha256}.",
            ],
            result.Reasons);
    }

    [Fact]
    public async Task A_set_yaml_edited_after_the_records_leaves_every_record_pinning_another_set()
    {
        var ledger = await Ledger.CreateAsync();
        var record = await ledger.DeployAsync(1, PassingIq);
        ledger.Records.Put(Ledger.Key("set.yaml"), "another set");

        var result = await ledger.ReadAsync();

        Assert.False(result.Qualified);
        Assert.Contains($"{Name(record)}: pins set {ledger.SetSha256}", OneReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_renamed_to_a_later_time_or_to_another_activity_or_to_no_time_is_a_reason()
    {
        var ledger = await Ledger.CreateAsync();
        var renamed = await ledger.DeployAsync(1, PassingIq);
        var qualify = await ledger.QualifyAsync(2, PassingIq);
        var deploy = await ledger.DeployAsync(3, PassingIq);
        var laterName = Ledger.Key("20261017T090000000Z-deploy.json");
        var otherActivity = Ledger.Key(Name(qualify).Replace("qualify", "deploy", StringComparison.Ordinal));
        var noTime = Ledger.Key("yesterday-deploy.json");
        ledger.Records.Files.Remove(renamed, out var renamedBytes);
        ledger.Records.Files[laterName] = renamedBytes!;
        ledger.Records.Files.Remove(qualify, out var qualifyBytes);
        ledger.Records.Files[otherActivity] = qualifyBytes!;
        ledger.Records.Files[noTime] = ledger.Records.Files[deploy];
        ledger.Forge(noTime, r => r["finishedAt"] = "0001-01-01T00:00:00Z"); // what an unparsed name would format to

        var result = await ledger.ReadAsync();

        Assert.False(result.Qualified);
        Assert.Equal(
            [
                $"{Name(otherActivity)}: records activity 'qualify', not 'deploy' as its name says.",
                $"{Name(laterName)}: finishedAt 2026-10-17T08:01:00Z does not match the time '20261017T090000000Z' in its name.",
                $"{Name(noTime)}: finishedAt 0001-01-01T00:00:00Z does not match the time 'yesterday' in its name.",
            ],
            result.Reasons);
    }

    [Fact]
    public async Task A_record_that_fails_the_schema_is_a_reason_for_an_extra_property_a_missing_identity_or_environment_type_and_a_deploy_without_iq()
    {
        var ledger = await Ledger.CreateAsync();
        var extra = await ledger.DeployAsync(1, PassingIq);
        var noIdentity = await ledger.DeployAsync(2, PassingIq);
        var noIq = await ledger.DeployAsync(3, PassingIq);
        var noType = await ledger.DeployAsync(4, PassingIq); // neither a type nor, as before types, a tier
        ledger.Forge(extra, r => r["approved"] = true);
        ledger.Forge(noIdentity, r => r.Remove("identity"));
        ledger.Forge(noIq, r => r.Remove("iq"));
        ledger.Forge(noType, r => ((JObject)r["environment"]!).Remove("type"));

        var result = await ledger.ReadAsync();

        Assert.False(result.Qualified);
        Assert.Equal(4, result.Reasons.Count);
        Assert.All(new[] { extra, noIdentity, noIq, noType }, path => Assert.Contains(result.Reasons, r => r.StartsWith($"{Name(path)}: is not a valid release record: ", StringComparison.Ordinal)));
        Assert.All(result.Reasons, r => Assert.DoesNotContain(Ledger.Key(string.Empty), r, StringComparison.Ordinal)); // the file name says where, not the full path again
    }

    [Fact]
    public async Task An_approval_record_at_a_release_record_path_is_a_reason_naming_its_kind()
    {
        var ledger = await Ledger.CreateAsync();
        var deploy = await ledger.DeployAsync(1, PassingIq);
        ledger.Records.Put(deploy, $$"""
            { "kind": "ApprovalRecord", "approvedAt": "2026-10-17T08:01:00Z", "environment": { "name": "stage", "type": "stage" },
              "release": { "label": "1.0.0", "setSha256": "{{new string('0', 64)}}" }, "qualifiedOn": "dev",
              "identity": { "tenantId": "tenant-1", "clientId": "client-1", "objectId": "object-1" }, "meaning": "placed by hand" }
            """);

        var result = await ledger.ReadAsync();

        Assert.False(result.Qualified);
        Assert.Equal($"{Name(deploy)}: is not a valid release record: {Name(deploy)}: kind: unknown kind 'ApprovalRecord'; expected one of ReleaseRecord.", OneReason(result));
        Assert.Empty(result.Deployers); // the file did not count as a deploy
    }

    [Fact]
    public async Task An_empty_file_a_file_that_is_not_json_and_a_file_that_cannot_be_read_are_reasons_not_crashes()
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        ledger.Records.Put(Ledger.Key("20261017T080200000Z-deploy.json"), string.Empty);
        ledger.Records.Put(Ledger.Key("20261017T080300000Z-qualify.json"), "[1, 2]");
        ledger.Records.Put(Ledger.Key("20261017T080400000Z-deploy.json"), "{}");
        ledger.Records.Unreadable.Add(Ledger.Key("20261017T080400000Z-deploy.json"));

        var result = await ledger.ReadAsync();

        Assert.False(result.Qualified);
        Assert.Equal(3, result.Reasons.Count);
        Assert.All(result.Reasons, r => Assert.Contains(": cannot be read: ", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_record_without_read_permission_is_a_reason_not_a_crash()
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        var noAccess = Ledger.Key("20261017T080200000Z-deploy.json");
        ledger.Records.Put(noAccess, "{}");
        ledger.Records.Unreadable.Add(noAccess);

        var result = await ledger.ReadAsync();

        Assert.StartsWith($"{Name(noAccess)}: cannot be read: ", OneReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_set_yaml_that_cannot_be_read_is_a_reason_and_an_unknown_set()
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        var setPath = Ledger.Key("set.yaml");
        ledger.Records.Unreadable.Add(setPath);

        var result = await ledger.ReadAsync();

        Assert.Null(result.SetSha256);
        Assert.StartsWith("the records of label 1.0.0 on dev cannot be read: ", OneReason(result), StringComparison.Ordinal);
        Assert.Contains(setPath, OneReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_label_directory_that_cannot_be_listed_is_a_reason_and_an_unknown_set()
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        ledger.Records.Unreadable.Add("dev/1.0.0"); // set.yaml is still readable, the names of the label are not

        var result = await ledger.ReadAsync();

        Assert.Null(result.SetSha256);
        Assert.StartsWith("the records of label 1.0.0 on dev cannot be read: ", OneReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_that_are_not_deploy_or_qualify_records_are_not_records_and_do_not_spoil_a_qualified_label()
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, PassingIq);
        ledger.Records.Put(Ledger.Key("20261017T080200000Z-approve.json"), "not json");
        ledger.Records.Put(Ledger.Key("notes.txt"), "not json");

        Assert.True((await ledger.ReadAsync()).Qualified);
    }

    [Fact]
    public async Task A_newer_failed_deploy_beats_an_older_passing_one_and_a_newer_passing_one_beats_an_older_failed_one()
    {
        var ledger = await Ledger.CreateAsync();
        var passing = await ledger.DeployAsync(1, PassingIq);
        var failing = await ledger.DeployAsync(2, null, failed: true);

        var failedLast = await ledger.ReadAsync();
        ledger.Records.Files.Remove(passing);
        ledger.Records.Files.Remove(failing);
        await ledger.DeployAsync(3, null, failed: true);
        await ledger.DeployAsync(4, PassingIq);
        var passedLast = await ledger.ReadAsync();

        Assert.Equal(
            [
                $"the newest deploy ({Name(failing)}) failed.",
                "workload shop: OQ is notRun.",
                "workload cart: OQ is notRun.",
                $"IQ of the newest record ({Name(failing)}) is notRun.",
            ],
            failedLast.Reasons);
        Assert.True(passedLast.Qualified, string.Join("; ", passedLast.Reasons));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("notRun")]
    public async Task An_oq_that_did_not_pass_is_a_reason_naming_the_workload(string status)
    {
        var ledger = await Ledger.CreateAsync();
        var record = await ledger.DeployAsync(1, PassingIq);
        ledger.Forge(record, r => r["workloads"]![0]!["oq"]!["status"] = status);

        var result = await ledger.ReadAsync();

        Assert.Equal($"workload shop: OQ is {status}.", OneReason(result));
    }

    [Fact]
    public async Task An_unchanged_oq_counts_only_when_an_earlier_deploy_passed_it_for_that_workload()
    {
        var ledger = await Ledger.CreateAsync();
        var first = await ledger.DeployAsync(1, PassingIq);
        var second = await ledger.DeployAsync(2, PassingIq);
        ledger.Forge(second, r => r["workloads"]!.ToList().ForEach(w => w["oq"]!["status"] = "unchanged"));
        var bothPassedBefore = await ledger.ReadAsync();
        ledger.Forge(first, r => r["workloads"]![1]!["oq"]!["status"] = "failed");
        var cartNeverPassed = await ledger.ReadAsync();

        Assert.True(bothPassedBefore.Qualified, string.Join("; ", bothPassedBefore.Reasons));
        Assert.Equal("workload cart: OQ is unchanged and no earlier deploy passed it.", OneReason(cartNeverPassed));
    }

    [Fact]
    public async Task An_unchanged_oq_in_the_only_deploy_is_not_qualified()
    {
        var ledger = await Ledger.CreateAsync();
        ledger.Forge(await ledger.DeployAsync(1, PassingIq), r => r["workloads"]![0]!["oq"]!["status"] = "unchanged");

        Assert.Equal("workload shop: OQ is unchanged and no earlier deploy passed it.", OneReason(await ledger.ReadAsync()));
    }

    [Fact]
    public async Task An_iq_that_failed_or_did_not_run_is_not_qualified()
    {
        var ledger = await Ledger.CreateAsync();
        var failed = await ledger.DeployAsync(1, FailingIq);
        var failedResult = await ledger.ReadAsync();
        ledger.Records.Files.Remove(failed);
        var notRun = await ledger.DeployAsync(2, null);

        var notRunResult = await ledger.ReadAsync();

        Assert.Equal($"IQ of the newest record ({Name(failed)}) is failed.", OneReason(failedResult));
        Assert.Equal($"IQ of the newest record ({Name(notRun)}) is notRun.", OneReason(notRunResult));
    }

    [Fact]
    public async Task An_iq_from_before_a_newer_deploy_cannot_vouch_for_it_and_a_later_passing_qualify_can()
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.QualifyAsync(1, PassingIq);
        var deploy = await ledger.DeployAsync(2, null);
        var afterDeploy = await ledger.ReadAsync();
        await ledger.QualifyAsync(3, PassingIq);
        var afterQualify = await ledger.ReadAsync();
        await ledger.QualifyAsync(4, FailingIq);
        var afterFailedQualify = await ledger.ReadAsync();

        Assert.Equal($"IQ of the newest record ({Name(deploy)}) is notRun.", OneReason(afterDeploy));
        Assert.True(afterQualify.Qualified, string.Join("; ", afterQualify.Reasons));
        Assert.Contains("is failed.", OneReason(afterFailedQualify), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_qualify_without_any_deploy_is_not_qualified()
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.QualifyAsync(1, PassingIq);

        Assert.Contains("never deployed", OneReason(await ledger.ReadAsync()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_deployers_are_the_distinct_principals_of_every_deploy_failed_attempts_included_and_not_of_a_qualify()
    {
        var ledger = await Ledger.CreateAsync();
        await ledger.DeployAsync(1, null, "object-1", failed: true);
        await ledger.DeployAsync(2, PassingIq, "Object-2");
        await ledger.DeployAsync(3, PassingIq, "object-2");
        await ledger.QualifyAsync(4, PassingIq);
        var qualifier = await ledger.QualifyAsync(5, PassingIq);
        ledger.Forge(qualifier, r => r["identity"]!["objectId"] = "object-3");

        var result = await ledger.ReadAsync();

        Assert.Equal(["object-1", "Object-2"], result.Deployers);
    }

    [Fact]
    public async Task Reading_changes_nothing_in_the_records_even_over_forged_records()
    {
        var ledger = await Ledger.CreateAsync();
        ledger.Forge(await ledger.DeployAsync(1, PassingIq), r => r["release"]!["label"] = "2.0.0");
        ledger.Records.Put(Ledger.Key("20261017T080200000Z-deploy.json"), "broken");
        string Snapshot() => string.Join('\n', ledger.Records.Files.Keys.Order(StringComparer.Ordinal).Select(key => $"{key}:{ledger.Records.Text(key)}"));
        var before = Snapshot();

        await ledger.ReadAsync();

        Assert.Equal(before, Snapshot());
    }
}
