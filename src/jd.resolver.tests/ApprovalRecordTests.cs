using System.Runtime.CompilerServices;
using jd.core.identity;
using jd.definitionvalidator;
using jd.resolver.environment;
using jd.resolver.release;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// The approval record of docs/architecture/approvals.md section 4: its schema and committed golden, that nothing typed can change a field,
// and the reader of approval files (section 5, step 3): files written by the real builder and forged by editing them, as someone with the records directory could. Regenerate the golden with JD_UPDATE_GOLDEN=1 and review the diff.
public sealed class ApprovalRecordTests : IDisposable
{
    private const string Alice = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Set = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Other = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private static readonly DateTimeOffset At = new(2026, 10, 17, 3, 4, 5, TimeSpan.Zero);
    private static readonly DeployerIdentity Principal = new("tenant-1", "client-1", Alice);
    private static readonly EnvironmentDescriptor Stage = new("stage", "region-1", "stage", new Dictionary<string, string>(), [], new ApprovalPolicy("dev", 1, [Alice], true));

    private readonly string _records = Directory.CreateTempSubdirectory("jd-approval-tests").FullName;

    public void Dispose() => Directory.Delete(_records, recursive: true);

    private static string Record(string meaning = "Reviewed the test evidence; ready for stage.") => ApprovalRecord.Build(Stage, "1.0.0", Set, Principal, meaning, At);

    private static JObject Parse(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static async Task<bool> IsValidAsync(JObject record)
    {
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schemas", "approval-record.schema.json"));
        return (await new YamlSchemaValidator().ValidateTokenAsync(schema, record)).IsValid;
    }

    private static string GoldenSource([CallerFilePath] string testFile = "") => Path.Combine(Path.GetDirectoryName(testFile)!, "golden", "approval-record.json");

    [Fact]
    public async Task An_approval_is_valid_against_the_schema_and_is_the_committed_golden_record()
    {
        var record = Record();

        Assert.True(await IsValidAsync(Parse(record)));
        if (System.Environment.GetEnvironmentVariable("JD_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(GoldenSource(), record);
        }

        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "approval-record.json")), record);
    }

    [Fact]
    public async Task Text_that_looks_like_json_or_an_identity_stays_one_string_and_changes_no_field()
    {
        const string Meaning = "\",\"identity\":{\"tenantId\":\"x\",\"clientId\":\"x\",\"objectId\":\"evil\"},\"x\":\"";

        var record = Parse(Record(Meaning));

        Assert.True(await IsValidAsync(record));
        Assert.Equal(Meaning, (string?)record["meaning"]);
        Assert.Equal(["kind", "approvedAt", "environment", "release", "qualifiedOn", "identity", "meaning"], record.Properties().Select(p => p.Name));
        Assert.Equal(["tenant-1", "client-1", Alice], record["identity"]!.Select(v => (string?)v));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("identity")]
    [InlineData("setSha256")]
    [InlineData("type")]
    [InlineData("noType")]
    [InlineData("emptyMeaning")]
    [InlineData("longMeaning")]
    [InlineData("newlineMeaning")]
    [InlineData("controlMeaning")]
    public async Task The_schema_refuses_an_extra_property_a_missing_field_and_a_bad_meaning(string defect)
    {
        var record = Parse(Record());
        switch (defect)
        {
            case "extra": record["approver"] = "someone"; break;
            case "identity": record.Remove("identity"); break;
            case "setSha256": record["release"]!["setSha256"] = "abc"; break;
            case "type": record["environment"]!["type"] = "Not A Type"; break;
            case "noType": ((JObject)record["environment"]!).Remove("type"); break;
            case "emptyMeaning": record["meaning"] = string.Empty; break;
            case "longMeaning": record["meaning"] = new string('m', 501); break;
            case "newlineMeaning": record["meaning"] = "ok\n"; break;
            default: record["meaning"] = "a\u0085b"; break;
        }

        Assert.False(await IsValidAsync(record));
    }

    [Fact]
    public async Task A_meaning_of_500_characters_is_valid()
    {
        Assert.True(await IsValidAsync(Parse(Record(new string('m', 500)))));
    }

    [Fact]
    public async Task An_environment_without_a_policy_cannot_be_approved_for()
    {
        var ungated = Stage with { Approval = null };

        Assert.Throws<ArgumentException>(() => ApprovalRecord.Build(ungated, "1.0.0", Set, Principal, "m", At));
        await Assert.ThrowsAsync<ArgumentException>(() => ApprovalRecord.ReadAsync(new LocalRecordStore(_records), ungated, "1.0.0", Set));
    }

    // An approval written by the real builder at the given second into <environment>/<label>, then edited by forge; returns the file name.
    private async Task<string> WriteApprovalAsync(string objectId, int second, Action<JObject>? forge = null, string environment = "stage", string label = "1.0.0")
    {
        var at = At.AddSeconds(second);
        var record = Parse(ApprovalRecord.Build(Stage, "1.0.0", Set, new DeployerIdentity("tenant-1", "client-1", objectId), "ready", at));
        forge?.Invoke(record);
        return Path.GetFileName(await ReleaseRecordFile.WriteAsync(new LocalRecordStore(_records), environment, label, at, ReleaseRecordFile.ApproveActivity, record.ToString(Formatting.Indented)));
    }

    // Records written before environment types carry a tier; they stay valid and count (components.md section 6).
    [Fact]
    public async Task An_approval_written_with_a_tier_before_environment_types_still_validates_and_counts()
    {
        await WriteApprovalAsync("object-a", 1, record => record["environment"] = new JObject { ["name"] = "stage", ["tier"] = "protected" });

        var reading = await ApprovalRecord.ReadAsync(new LocalRecordStore(_records), Stage, "1.0.0", Set);

        Assert.Equal(["object-a"], reading.Approvers);
        Assert.Empty(reading.Ignored);
    }

    [Fact]
    public async Task The_approvers_are_the_distinct_principals_of_valid_approvals_of_this_set_and_nothing_else_is_reported_as_ignored()
    {
        var none = await ApprovalRecord.ReadAsync(new LocalRecordStore(_records), Stage, "1.0.0", Set); // no directory yet
        Assert.Empty(none.Approvers);
        Assert.Empty(none.Ignored);
        await WriteApprovalAsync("object-a", 1);
        await WriteApprovalAsync("OBJECT-A", 2); // the same principal again
        await WriteApprovalAsync("object-b", 3);
        await WriteApprovalAsync("object-g", 4, label: "2.0.0"); // another label's directory
        await WriteApprovalAsync("object-h", 5, environment: "other"); // another environment's directory
        await File.WriteAllTextAsync(Path.Combine(_records, "stage", "1.0.0", "20261017T030410000Z-deploy.json"), "not an approval file");

        var reading = await ApprovalRecord.ReadAsync(new LocalRecordStore(_records), Stage, "1.0.0", Set);

        Assert.Equal(["object-a", "object-b"], reading.Approvers.Select(a => a.ToLowerInvariant()).Order(StringComparer.Ordinal));
        Assert.Equal(2, reading.Approvers.Count);
        Assert.Empty(reading.Ignored);
    }

    [Theory]
    [InlineData("setSha256", "approves set '" + Other + "', not '" + Set + "'.")]
    [InlineData("environment", "approves environment 'other', not 'stage'.")]
    [InlineData("label", "approves label '2.0.0', not '1.0.0'.")]
    [InlineData("qualifiedOn", "approves qualifiedOn 'other', not 'dev'.")]
    public async Task An_approval_that_names_another_set_environment_label_or_qualifiedOn_is_ignored_and_reported(string field, string reason)
    {
        var name = await WriteApprovalAsync("object-a", 1, record =>
        {
            switch (field)
            {
                case "setSha256": record["release"]!["setSha256"] = Other; break;
                case "environment": record["environment"]!["name"] = "other"; break;
                case "label": record["release"]!["label"] = "2.0.0"; break;
                default: record["qualifiedOn"] = "other"; break;
            }
        });

        var reading = await ApprovalRecord.ReadAsync(new LocalRecordStore(_records), Stage, "1.0.0", Set);

        Assert.Empty(reading.Approvers);
        Assert.Equal($"{name}: {reason}", Assert.Single(reading.Ignored));
    }

    [Fact]
    public async Task An_approval_file_of_another_kind_is_ignored_naming_its_kind()
    {
        var name = await WriteApprovalAsync("object-a", 1, record => record["kind"] = "ReleaseRecord");

        var reading = await ApprovalRecord.ReadAsync(new LocalRecordStore(_records), Stage, "1.0.0", Set);

        Assert.Empty(reading.Approvers);
        Assert.Equal($"{name}: is not a valid approval record: {name}: kind: unknown kind 'ReleaseRecord'; expected one of ApprovalRecord.", Assert.Single(reading.Ignored));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("identity")]
    [InlineData("objectType")]
    [InlineData("emptyMeaning")]
    public async Task An_approval_that_fails_the_schema_is_ignored_and_reported(string defect)
    {
        var name = await WriteApprovalAsync("object-a", 1, record =>
        {
            switch (defect)
            {
                case "extra": record["approver"] = "someone"; break;
                case "identity": record.Remove("identity"); break;
                case "objectType": record["identity"]!["objectId"] = new JObject(); break;
                default: record["meaning"] = string.Empty; break;
            }
        });

        var reading = await ApprovalRecord.ReadAsync(new LocalRecordStore(_records), Stage, "1.0.0", Set);

        Assert.Empty(reading.Approvers);
        Assert.StartsWith($"{name}: is not a valid approval record: ", Assert.Single(reading.Ignored), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task A_file_that_is_not_a_json_object_is_ignored_and_reported_not_a_crash(string content)
    {
        await WriteApprovalAsync("object-a", 1);
        var directory = Path.Combine(_records, "stage", "1.0.0");
        await File.WriteAllTextAsync(Path.Combine(directory, "20261017T030459000Z-approve.json"), content);

        var reading = await ApprovalRecord.ReadAsync(new LocalRecordStore(_records), Stage, "1.0.0", Set);

        Assert.Equal(["object-a"], reading.Approvers);
        Assert.StartsWith("20261017T030459000Z-approve.json: cannot be read: ", Assert.Single(reading.Ignored), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("notes-approve.json")]
    [InlineData("-approve.json")]
    [InlineData("20261017T0304-approve.json")]
    [InlineData("20261017T030499000Z-extra-approve.json")]
    public async Task An_approval_file_not_named_with_a_time_is_ignored_and_reported_even_if_its_content_is_valid(string fileName)
    {
        var valid = await WriteApprovalAsync("object-b", 2);
        var directory = Path.Combine(_records, "stage", "1.0.0");
        File.Move(Path.Combine(directory, valid), Path.Combine(directory, fileName));

        var reading = await ApprovalRecord.ReadAsync(new LocalRecordStore(_records), Stage, "1.0.0", Set);

        Assert.Empty(reading.Approvers);
        Assert.StartsWith($"{fileName}: is not named <time>-approve.json", Assert.Single(reading.Ignored), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approval_without_read_permission_is_ignored_and_reported_not_a_crash()
    {
        await WriteApprovalAsync("object-a", 1);
        var noAccess = Path.Combine(_records, "stage", "1.0.0", await WriteApprovalAsync("object-b", 2));
        if (OperatingSystem.IsWindows() || System.Environment.IsPrivilegedProcess)
        {
            return; // file modes do not stop a privileged process, and Windows has none
        }

        File.SetUnixFileMode(noAccess, UnixFileMode.None);

        var reading = await ApprovalRecord.ReadAsync(new LocalRecordStore(_records), Stage, "1.0.0", Set);

        Assert.Equal(["object-a"], reading.Approvers);
        Assert.StartsWith($"{Path.GetFileName(noAccess)}: cannot be read: ", Assert.Single(reading.Ignored), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_label_directory_that_cannot_be_listed_is_reported_not_a_crash()
    {
        await WriteApprovalAsync("object-a", 1);
        var directory = Path.Combine(_records, "stage", "1.0.0");
        if (OperatingSystem.IsWindows() || System.Environment.IsPrivilegedProcess)
        {
            return;
        }

        File.SetUnixFileMode(directory, UnixFileMode.UserExecute);
        try
        {
            var reading = await ApprovalRecord.ReadAsync(new LocalRecordStore(_records), Stage, "1.0.0", Set);

            Assert.Empty(reading.Approvers);
            Assert.StartsWith("the records of label 1.0.0 on stage cannot be read: ", Assert.Single(reading.Ignored), StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
