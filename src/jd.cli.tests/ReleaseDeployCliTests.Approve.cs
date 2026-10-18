using jd.core.identity;
using jd.core.revisions;
using Xunit;

namespace jd.cli.tests;

// `jd approve` (docs/architecture/approvals.md section 4): the sample set is deployed (and qualified) on dev for real through the CLI, the approval is for a gated
// `stage` descriptor; only the backend, the live revisions, the clock and the principal are fakes.
public sealed partial class ReleaseDeployCliTests
{
    private const string Alice = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Bob = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Carol = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string Dave = "dddddddd-dddd-dddd-dddd-dddddddddddd";

    private string StageDirectory => Path.Combine(_records, "stage", "1.0.0");

    private string[] ApprovalFiles => Directory.Exists(StageDirectory) ? Directory.GetFiles(StageDirectory) : [];

    private void Act(string objectId) => _identity.Value = new DeployerIdentity("tenant-1", "client-1", objectId);

    // The sample set deployed on dev by deployer, with IQ passed unless the live revisions say otherwise.
    private async Task DeployedAsync(string deployer = "object-1", Func<string, string>? changeApp = null)
    {
        var set = await CreateSetAsync(changeApp);
        Act(deployer);
        await RunAsync(new RecordingBackend(), Deploy(set));
    }

    // dev's descriptor as a gated `stage` that needs the set qualified on dev.
    private async Task<string> StageAsync(int required = 1, string[]? approvers = null, bool separate = true)
    {
        var list = string.Join(", ", (approvers ?? [Alice]).Select(a => $"\"{a}\""));
        var text = (await File.ReadAllTextAsync(Environment)).Replace("name: dev", "name: stage", StringComparison.Ordinal).Replace("type: dev", "type: stage", StringComparison.Ordinal);
        var path = Path.Combine(_temp, "stage.env.yaml");
        await File.WriteAllTextAsync(path, text + $"approval:\n  qualifiedOn: dev\n  required: {required}\n  approvers: [{list}]\n  separateFromDeployer: {(separate ? "true" : "false")}\n");
        return path;
    }

    private Task<(int Code, string Out, string Err)> ApproveAsync(string stage, string meaning = "ready for stage", string label = "1.0.0") =>
        RunAsync(null, "approve", label, "--env", stage, "--meaning", meaning);

    // An approval file as `jd approve` would write it, or the given text, placed by hand.
    private void Place(string name, string objectId, string? content = null)
    {
        Directory.CreateDirectory(StageDirectory);
        File.WriteAllText(Path.Combine(StageDirectory, name), content
            ?? $$"""
            { "kind": "ApprovalRecord", "approvedAt": "2026-01-01T00:00:00Z", "environment": { "name": "stage", "tier": "protected" },
              "release": { "label": "1.0.0", "setSha256": "{{Sha256(Path.Combine(_records, "dev", "1.0.0", "set.yaml"))}}" }, "qualifiedOn": "dev",
              "identity": { "tenantId": "tenant-1", "clientId": "client-1", "objectId": "{{objectId}}" }, "meaning": "placed by hand" }
            """);
    }

    // Sets a file mode, or says false when it would change nothing: file modes do not stop a privileged process, and Windows has none.
    private static bool TryRestrict(string path)
    {
        if (OperatingSystem.IsWindows() || System.Environment.IsPrivilegedProcess)
        {
            return false;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        return true;
    }

    private void AssertRefused(int code, string stderr, string cause)
    {
        Assert.Equal(1, code);
        Assert.Contains($"jd: approve: ", stderr);
        Assert.Contains(cause, stderr);
        Assert.Empty(ApprovalFiles);
    }

    [Fact]
    public async Task An_approval_writes_one_record_in_the_targets_directory_naming_the_ports_principal_the_kept_set_and_the_meaning()
    {
        await DeployedAsync();
        var stage = await StageAsync();
        _identity.Value = new DeployerIdentity("the-tenant", "the-client", Alice);
        var devFiles = Directory.GetFiles(LabelDirectory).Length;

        var (code, stdout, stderr) = await ApproveAsync(stage, "Reviewed it; ready.");

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        var file = Assert.Single(ApprovalFiles);
        Assert.Equal($"approved 1.0.0 for stage (1 of 1)\nrecord: {file}\n", stdout.ReplaceLineEndings("\n"));
        var record = Read(file);
        Assert.Equal("ApprovalRecord", (string?)record["kind"]);
        var approvedAt = (string)record["approvedAt"]!;
        Assert.StartsWith("2026-10-17T08:", approvedAt);
        Assert.Equal(approvedAt.Replace("-", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal)[..^1] + "000Z-approve.json", Path.GetFileName(file));
        Assert.Equal("stage", (string?)record["environment"]!["name"]);
        Assert.Equal("stage", (string?)record["environment"]!["type"]);
        Assert.Equal("1.0.0", (string?)record["release"]!["label"]);
        Assert.Equal(Sha256(Path.Combine(_records, "dev", "1.0.0", "set.yaml")), (string?)record["release"]!["setSha256"]);
        Assert.Equal("dev", (string?)record["qualifiedOn"]);
        Assert.Equal(["the-tenant", "the-client", Alice], record["identity"]!.Select(v => (string?)v));
        Assert.Equal("Reviewed it; ready.", (string?)record["meaning"]);
        Assert.Equal(devFiles, Directory.GetFiles(LabelDirectory).Length);
    }

    [Fact]
    public async Task The_identity_is_the_ports_whatever_a_definition_or_the_meaning_contains_and_the_meaning_is_only_a_string()
    {
        await DeployedAsync(changeApp: app => app.Replace("LOG_LEVEL: info", "LOG_LEVEL: '\",\"identity\":{\"objectId\":\"evil-definition\"}'", StringComparison.Ordinal));
        var stage = await StageAsync();
        Act(Alice);
        const string Meaning = "\",\"identity\":{\"tenantId\":\"x\",\"clientId\":\"x\",\"objectId\":\"evil-meaning\"},\"x\":\"";

        var (code, _, stderr) = await ApproveAsync(stage, Meaning);

        Assert.True(code == 0, stderr);
        var file = Assert.Single(ApprovalFiles);
        var record = Read(file);
        Assert.Equal(["kind", "approvedAt", "environment", "release", "qualifiedOn", "identity", "meaning"], record.Properties().Select(p => p.Name));
        Assert.Equal(Alice, (string?)record["identity"]!["objectId"]);
        Assert.Equal(Meaning, (string?)record["meaning"]);
        Assert.DoesNotContain("evil-definition", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        Assert.Contains("evil-definition", await File.ReadAllTextAsync(Path.Combine(_records, "dev", "1.0.0", "set.yaml")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\u007fb")]
    [InlineData("a\u0085b")]
    [InlineData("")]
    public async Task A_meaning_with_a_control_character_or_none_at_all_is_a_usage_error_and_nothing_is_read_or_written(string meaning)
    {
        await DeployedAsync();
        var stage = await StageAsync();
        var calls = _identity.Calls;

        var (code, _, stderr) = await ApproveAsync(stage, meaning);

        Assert.Equal(2, code);
        Assert.Contains("--meaning <text>: 1 to 500 characters, no control characters", stderr);
        Assert.Empty(ApprovalFiles);
        Assert.Equal(calls, _identity.Calls);
    }

    [Fact]
    public async Task A_meaning_may_have_500_characters_and_not_501()
    {
        await DeployedAsync();
        var stage = await StageAsync();
        Act(Alice);

        Assert.Equal(2, (await ApproveAsync(stage, new string('m', 501))).Code);
        Assert.Empty(ApprovalFiles);
        Assert.Equal(0, (await ApproveAsync(stage, new string('m', 500))).Code);
        Assert.Single(ApprovalFiles);
    }

    [Theory]
    [InlineData]
    [InlineData("1.0.0")]
    [InlineData("1.0.0", "--env", "e.yaml")]
    [InlineData("1.0.0", "--meaning", "m")]
    [InlineData("--env", "e.yaml", "--meaning", "m")]
    [InlineData("1.0.0", "2.0.0", "--env", "e.yaml", "--meaning", "m")]
    [InlineData("1.0.0", "--env", "e.yaml", "--meaning", "m", "--catalog", "c")]
    [InlineData("1.0.0", "--env", "e.yaml", "--meaning")]
    [InlineData("1.0.0", "--env")]
    public async Task Usage_errors_exit_2_and_print_the_usage_before_anything_is_read(params string[] args)
    {
        var (code, _, stderr) = await RunAsync(null, ["approve", .. args]);

        Assert.Equal(2, code);
        Assert.Contains("Usage:", stderr);
        Assert.Equal(0, _identity.Calls);
    }

    [Fact]
    public async Task An_unknown_option_is_named_in_the_usage_error()
    {
        var (code, _, stderr) = await RunAsync(null, "approve", "1.0.0", "--env", "e.yaml", "--meaning", "m", "--bogus");

        Assert.Equal(2, code);
        Assert.Contains("jd: unknown option '--bogus'.", stderr);
    }

    [Fact]
    public async Task A_descriptor_that_cannot_be_read_is_exit_2_and_one_that_is_not_valid_is_exit_1()
    {
        var absent = Path.Combine(_temp, "absent.yaml");
        var invalid = Path.Combine(_temp, "invalid.yaml");
        await File.WriteAllTextAsync(invalid, "kind: Environment\n");

        var (missingCode, _, missing) = await ApproveAsync(absent);
        var (invalidCode, _, error) = await ApproveAsync(invalid);

        Assert.Equal(2, missingCode);
        Assert.Contains($"cannot read '{absent}'", missing);
        Assert.Equal(1, invalidCode);
        Assert.Contains("invalid.yaml", error);
        Assert.Equal(0, _identity.Calls);
    }

    [Theory]
    [InlineData("../1.0.0", "cannot name a directory")]
    [InlineData("a/b", "cannot name a directory")]
    [InlineData("a\\b", "cannot name a directory")]
    [InlineData("1.0.0-a..b", "cannot name a directory")]
    [InlineData("1.0.0\n", "is not a release label")]
    [InlineData("foo", "is not a release label")]
    [InlineData(".", "is not a release label")]
    public async Task A_label_that_is_not_label_shaped_is_a_usage_error_before_the_descriptor_the_records_and_the_credential(string label, string expected)
    {
        var (code, _, stderr) = await ApproveAsync(Environment, label: label); // an ungated descriptor: only the label can be what is refused

        Assert.Equal(2, code);
        Assert.Contains(expected, stderr);
        Assert.Contains("Usage:", stderr);
        Assert.Equal(0, _identity.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    [Fact]
    public async Task Refusal_1_an_environment_without_an_approval_policy_has_nothing_to_approve()
    {
        await DeployedAsync();
        var calls = _identity.Calls;

        var (code, _, stderr) = await ApproveAsync(Environment);

        AssertRefused(code, stderr, "environment 'dev' has no approval policy");
        Assert.Equal(calls, _identity.Calls);
        Assert.False(Directory.Exists(Path.Combine(_records, "stage")));
    }

    [Fact]
    public async Task Refusal_2_an_unknown_label_is_refused_as_unknown_and_creates_no_directory()
    {
        var stage = await StageAsync();
        Act(Alice);

        var (code, _, stderr) = await ApproveAsync(stage);

        AssertRefused(code, stderr, "cannot approve 1.0.0: unknown label");
        Assert.False(Directory.Exists(Path.Combine(_records, "stage")));
    }

    [Fact]
    public async Task Refusal_2_a_set_yaml_that_cannot_be_read_says_so_and_is_not_called_an_unknown_label()
    {
        await DeployedAsync();
        var stage = await StageAsync();
        Act(Alice);
        if (!TryRestrict(Path.Combine(_records, "dev", "1.0.0", "set.yaml")))
        {
            return;
        }

        var (code, _, stderr) = await ApproveAsync(stage);

        AssertRefused(code, stderr, "cannot approve 1.0.0: ");
        Assert.Contains("cannot be read", stderr);
        Assert.DoesNotContain("unknown label", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refusal_3_a_set_that_is_not_qualified_is_refused_with_every_reason()
    {
        _live.Answer = _ => [new LiveRevision("app--oob1", 100, ["other-image"])];
        await DeployedAsync();
        var stage = await StageAsync();
        Act(Alice);

        var (code, _, stderr) = await ApproveAsync(stage);

        AssertRefused(code, stderr, "release 1.0.0 is not qualified on dev:");
        Assert.Contains("  IQ of the newest record (", stderr);
        Assert.DoesNotContain("unknown label", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refusal_4_a_principal_that_is_not_listed_is_refused()
    {
        await DeployedAsync();
        var stage = await StageAsync(approvers: [Alice]);
        Act(Carol);

        var (code, _, stderr) = await ApproveAsync(stage);

        AssertRefused(code, stderr, $"principal {Carol} is not listed in the approvers of 'stage'.");
    }

    [Fact]
    public async Task Refusal_5_the_deployer_may_not_approve_their_own_set_when_the_policy_separates_and_may_when_it_does_not()
    {
        await DeployedAsync(deployer: Alice);
        var strict = await StageAsync(separate: true);

        var (code, _, stderr) = await ApproveAsync(strict);

        AssertRefused(code, stderr, $"principal {Alice} deployed 1.0.0 on dev, and 'stage' requires approvers separate from the deployer.");
        Assert.Equal(0, (await ApproveAsync(await StageAsync(separate: false))).Code);
        Assert.Single(ApprovalFiles);
    }

    [Fact]
    public async Task Refusal_6_a_repeat_by_the_same_principal_is_refused_and_a_second_principal_is_accepted_when_two_are_required()
    {
        await DeployedAsync();
        var stage = await StageAsync(required: 2, approvers: [Alice, Bob]);
        Act(Alice);

        var (first, firstOut, _) = await ApproveAsync(stage);
        var (again, _, stderr) = await ApproveAsync(stage);

        Assert.Equal(0, first);
        Assert.Contains("approved 1.0.0 for stage (1 of 2)", firstOut);
        Assert.Equal(1, again);
        Assert.Contains($"principal {Alice} already approved 1.0.0 for 'stage'.", stderr);
        Assert.Single(ApprovalFiles);
        Act(Bob);
        var (second, secondOut, _) = await ApproveAsync(stage);
        Assert.Equal(0, second);
        Assert.Contains("approved 1.0.0 for stage (2 of 2)", secondOut);
        Assert.Equal(2, ApprovalFiles.Length);
    }

    [Fact]
    public async Task A_repeat_is_found_among_hand_placed_approvals_of_the_same_set_whatever_the_case_of_the_id()
    {
        await DeployedAsync();
        var stage = await StageAsync();
        Place("20260101T000000000Z-approve.json", Alice.ToUpperInvariant());
        Act(Alice);

        var (code, _, stderr) = await ApproveAsync(stage);

        Assert.Equal(1, code);
        Assert.Contains("already approved", stderr);
        Assert.Single(ApprovalFiles);
    }

    [Fact]
    public async Task The_count_shown_is_the_distinct_listed_non_deployers_among_the_earlier_approvals_and_this_one()
    {
        await DeployedAsync(deployer: Bob);
        var stage = await StageAsync(required: 2, approvers: [Alice, Bob, Carol]);
        Place("20260101T000001000Z-approve.json", Bob); // the deployer
        Place("20260101T000002000Z-approve.json", Dave); // not listed
        Place("20260101T000003000Z-approve.json", Carol);
        Place("20260101T000004000Z-approve.json", Carol.ToUpperInvariant()); // the same principal again
        Act(Alice);

        var (code, stdout, _) = await ApproveAsync(stage);

        Assert.Equal(0, code);
        Assert.Contains("approved 1.0.0 for stage (2 of 2)", stdout);
    }

    [Fact]
    public async Task A_bad_credential_stops_before_the_records_are_read()
    {
        var stage = await StageAsync(); // no label was ever deployed: reading the records would say "unknown label"
        _identity.Failure = new InvalidOperationException("ARM_TENANT_ID is not set.");

        var (code, _, stderr) = await ApproveAsync(stage);

        Assert.Equal(1, code);
        Assert.Contains("jd: ARM_TENANT_ID is not set.", stderr);
        Assert.DoesNotContain("unknown label", stderr, StringComparison.Ordinal);
        Assert.Empty(ApprovalFiles);
    }

    [Fact]
    public async Task An_approval_file_is_never_opened_twice_an_existing_path_fails_and_the_old_bytes_stay()
    {
        await DeployedAsync();
        var stage = await StageAsync();
        Act(Alice);
        _clock = new StepClock(); // the same readings again: the approval's path is 20261017T080000000Z-approve.json
        var existing = Path.Combine(StageDirectory, "20261017T080000000Z-approve.json");
        Place(Path.GetFileName(existing), Alice, "old bytes");

        var (code, _, stderr) = await ApproveAsync(stage);

        Assert.Equal(1, code);
        Assert.Contains("the approval could not be written", stderr);
        Assert.Contains("run the command again", stderr);
        Assert.Equal("old bytes", await File.ReadAllTextAsync(existing));
        Assert.Single(ApprovalFiles);
    }

    [Fact]
    public async Task An_approval_file_that_cannot_be_used_is_ignored_reported_on_stderr_and_the_approval_goes_ahead()
    {
        await DeployedAsync();
        var stage = await StageAsync();
        Act(Alice);
        Place("20260101T000000000Z-approve.json", Bob, "not json");
        Place("notes-approve.json", Bob);

        var (code, stdout, stderr) = await ApproveAsync(stage);

        Assert.Equal(0, code);
        Assert.Contains("approved 1.0.0 for stage (1 of 1)", stdout);
        Assert.Contains("jd: approve: ignored 20260101T000000000Z-approve.json: cannot be read: ", stderr);
        Assert.Contains("jd: approve: ignored notes-approve.json: is not named <time>-approve.json", stderr);
        Assert.Equal(3, ApprovalFiles.Length);
    }

    [Fact]
    public async Task Help_lists_the_approve_command()
    {
        var (_, stdout, _) = await RunAsync(null, "--help");

        Assert.Contains("jd approve <label> --env <environment.yaml> --meaning <text>", stdout);
    }
}
