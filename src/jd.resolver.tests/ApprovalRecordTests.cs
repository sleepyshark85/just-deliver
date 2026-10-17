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
// and the minimal look at earlier approvals that `jd approve` needs. Regenerate the golden with JD_UPDATE_GOLDEN=1 and review the diff.
public sealed class ApprovalRecordTests : IDisposable
{
    private const string Alice = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Set = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset At = new(2026, 10, 17, 3, 4, 5, TimeSpan.Zero);
    private static readonly DeployerIdentity Principal = new("tenant-1", "client-1", Alice);
    private static readonly EnvironmentDescriptor Stage = new("stage", "region-1", "protected", new Dictionary<string, string>(), [], new ApprovalPolicy("dev", 1, [Alice], true));

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
    [InlineData("tier")]
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
            case "tier": record["environment"]!["tier"] = "production"; break;
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
        await Assert.ThrowsAsync<ArgumentException>(() => ApprovalRecord.ApproversAsync(_records, ungated, "1.0.0", Set));
    }

    private void Place(string name, string content, string environment = "stage", string label = "1.0.0")
    {
        var directory = Path.Combine(_records, environment, label);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
    }

    private static string Approval(string objectId, string environment = "stage", string set = Set, string qualifiedOn = "dev") =>
        $$"""{ "environment": { "name": "{{environment}}" }, "release": { "setSha256": "{{set}}" }, "qualifiedOn": "{{qualifiedOn}}", "identity": { "objectId": "{{objectId}}" } }""";

    [Fact]
    public async Task The_approvers_are_the_distinct_principals_of_the_approvals_of_this_set_environment_and_qualifiedOn_and_nothing_else()
    {
        Assert.Empty(await ApprovalRecord.ApproversAsync(_records, Stage, "1.0.0", Set)); // no directory yet
        Place("20261017T000001000Z-approve.json", Approval("object-a"));
        Place("20261017T000002000Z-approve.json", Approval("OBJECT-A")); // the same principal again
        Place("20261017T000003000Z-approve.json", Approval("object-b"));
        Place("20261017T000004000Z-approve.json", Approval("object-c", set: new string('f', 64)));
        Place("20261017T000005000Z-approve.json", Approval("object-d", environment: "other"));
        Place("20261017T000006000Z-approve.json", Approval("object-e", qualifiedOn: "other"));
        Place("20261017T000007000Z-approve.json", "not json");
        Place("20261017T000008000Z-approve.json", """{ "environment": "stage", "release": 1, "identity": [] }""");
        Place("20261017T000009000Z-approve.json", """{ "environment": { "name": "stage" }, "release": { "setSha256": "%SET%" }, "qualifiedOn": "dev" }""".Replace("%SET%", Set, StringComparison.Ordinal)); // no identity
        Place("20261017T000010000Z-deploy.json", Approval("object-f")); // not an approval file
        Place("20261017T000011000Z-approve.json", Approval("object-g"), label: "2.0.0"); // another label

        var approvers = await ApprovalRecord.ApproversAsync(_records, Stage, "1.0.0", Set);

        Assert.Equal(["object-a", "object-b"], approvers.Select(a => a.ToLowerInvariant()).Order(StringComparer.Ordinal));
        Assert.Equal(2, approvers.Count);
    }
}
