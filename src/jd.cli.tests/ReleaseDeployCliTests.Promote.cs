using System.Globalization;
using jd.core.revisions;
using Xunit;

namespace jd.cli.tests;

// `jd promote` (docs/architecture/approvals.md section 5): the sample set is deployed (and qualified) on dev for real through the CLI, approvals are made with
// `jd approve` or placed by hand, and the promotion runs the real release walk on a gated `stage` against the fake backend.
public sealed partial class ReleaseDeployCliTests
{
    private const string NoApproval = "20260101T000000000Z-approve.json";

    // Stand-ins in a test's arguments for the paths known only when it runs: stage's descriptor and the catalog.
    private const string StagePath = "<stage>";
    private const string CatalogPath = "<catalog>";

    private Task<(int Code, string Out, string Err)> PromoteAsync(string stage, RecordingBackend? backend = null, string label = "1.0.0") =>
        RunAsync(backend, "promote", label, "--to", stage, "--catalog", Catalog);

    private string[] StageFiles(string pattern) => Directory.Exists(StageDirectory) ? Directory.GetFiles(StageDirectory, pattern) : [];

    private string DevSetBytes => File.ReadAllText(Path.Combine(_records, "dev", "1.0.0", "set.yaml"));

    // dev deployed and qualified, `stage` gated, Alice approved it with `jd approve`; Dave, who is no approver, runs what comes next.
    private async Task<string> ApprovedAsync()
    {
        await DeployedAsync();
        var stage = await StageAsync();
        Act(Alice);
        Assert.Equal(0, (await ApproveAsync(stage)).Code);
        Act(Dave);
        return stage;
    }

    // A refused promotion: exit 1, nothing printed on stdout, no backend or live call, nothing on the target but the approvals that were there, and the identity
    // asked for exactly as often as the order says (once when the descriptor is gated, as the credential is checked before the records are read; else never).
    private async Task<string> RefusedAsync(string stage, string label = "1.0.0", int identityAsked = 1)
    {
        var backend = new RecordingBackend();
        var identityCalls = _identity.Calls;
        var liveCalls = _live.Calls.Count;

        var (code, stdout, stderr) = await PromoteAsync(stage, backend, label);

        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Empty(backend.Calls);
        Assert.Equal(identityCalls + identityAsked, _identity.Calls);
        Assert.Equal(liveCalls, _live.Calls.Count);
        Assert.DoesNotContain(StageFiles("*"), file => !file.EndsWith("-approve.json", StringComparison.Ordinal));
        return stderr;
    }

    [Fact]
    public async Task A_promotion_runs_the_walk_on_the_target_with_the_kept_bytes_and_records_them_with_iq_whoever_runs_it()
    {
        var stage = await ApprovedAsync(); // Dave is not an approver, nor the deployer
        var other = await CreateSetAsync(app => app.Replace("LOG_LEVEL: info", "LOG_LEVEL: warn", StringComparison.Ordinal), "other.yaml"); // same label, other bytes, elsewhere
        var backend = new RecordingBackend();
        var identityCalls = _identity.Calls;

        var (code, stdout, stderr) = await PromoteAsync(stage, backend);

        Assert.True(code == 0, stderr);
        Assert.Empty(stderr);
        var hash = Sha256(Path.Combine(_records, "dev", "1.0.0", "set.yaml"));
        var lines = stdout.ReplaceLineEndings("\n");
        Assert.StartsWith($"promoting 1.0.0 from dev (set sha256 {hash})\nworkload {Worker}\n", lines);
        Assert.Contains("release 1.0.0 on stage: 2 deployed, 0 unchanged, 0 failed, 0 not started.", lines);
        Assert.Contains("release 1.0.0 on stage: IQ passed, ", lines);
        Assert.Contains($"deploy {Worker}.stage._workload.runtime", backend.Calls);
        Assert.Contains($"deploy {App}.stage._workload.runtime", backend.Calls);
        Assert.DoesNotContain(backend.Calls, call => call.Contains(".dev.", StringComparison.Ordinal));
        Assert.Equal(DevSetBytes, File.ReadAllText(Path.Combine(StageDirectory, "set.yaml")));
        Assert.NotEqual(File.ReadAllText(other), DevSetBytes);
        var record = Read(Assert.Single(StageFiles("*-deploy.json")));
        Assert.Contains($"record: {Path.Combine(StageDirectory, "")}", stdout);
        Assert.Equal(hash, (string?)record["release"]!["setSha256"]);
        Assert.Equal("stage", (string?)record["environment"]!["name"]);
        Assert.Equal(Dave, (string?)record["identity"]!["objectId"]);
        Assert.Equal(1, _identity.Calls - identityCalls); // asked once, before the check, and not again by the walk
        Assert.Equal("succeeded", (string?)record["outcome"]);
        Assert.Equal("passed", (string?)record["iq"]!["status"]);
        var dev = Read(Assert.Single(RecordFiles));
        Assert.Equal(dev["workloads"]!.Select(w => (string?)w["image"]), record["workloads"]!.Select(w => (string?)w["image"]));
        Assert.Equal(dev["workloads"]!.Select(w => (string?)w["definitionSha256"]), record["workloads"]!.Select(w => (string?)w["definitionSha256"]));
    }

    [Fact]
    public async Task A_promotion_does_not_open_the_gate_for_a_direct_deploy_afterwards()
    {
        var stage = await ApprovedAsync();
        Assert.Equal(0, (await PromoteAsync(stage, new RecordingBackend())).Code);
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Command("release deploy", Path.Combine(_temp, "set.yaml"), stage));

        Assert.Equal(1, code);
        Assert.Contains("jd: release deploy: environment 'stage' is gated by an approval policy: use `jd promote`.", stderr);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task Every_ignored_approval_is_printed_even_when_the_promotion_goes_ahead()
    {
        var stage = await ApprovedAsync();
        Place("20260101T000001000Z-approve.json", Alice, $$"""{ "kind": "ApprovalRecord", "release": { "setSha256": "{{new string('0', 64)}}" } }""");

        var (code, _, stderr) = await PromoteAsync(stage, new RecordingBackend());

        Assert.Equal(0, code);
        Assert.Contains("jd: promote: ignored 20260101T000001000Z-approve.json: ", stderr);
    }

    [Fact]
    public async Task A_failed_walk_on_the_target_keeps_the_set_and_records_the_failure_as_release_deploy_does()
    {
        var stage = await ApprovedAsync();

        var (code, _, stderr) = await PromoteAsync(stage, new RecordingBackend { FailOn = $"{Worker}.stage.database.container" });

        Assert.Equal(1, code);
        Assert.Contains($"jd: stopped at {Worker}, node {Worker}/stage/database/container", stderr);
        Assert.Equal(DevSetBytes, File.ReadAllText(Path.Combine(StageDirectory, "set.yaml")));
        var record = Read(Assert.Single(StageFiles("*-deploy.json")));
        Assert.Equal("failed", (string?)record["outcome"]);
        Assert.Equal("notRun", (string?)record["iq"]!["status"]);
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_loaded_stops_the_walk_before_anything_is_deployed_or_kept()
    {
        var stage = await ApprovedAsync();
        var backend = new RecordingBackend();
        var empty = Directory.CreateDirectory(Path.Combine(_temp, "empty-catalog")).FullName;

        var (code, _, stderr) = await RunAsync(backend, "promote", "1.0.0", "--to", stage, "--catalog", empty);

        Assert.Equal(1, code);
        Assert.NotEmpty(stderr);
        Assert.Empty(backend.Calls);
        Assert.Empty(StageFiles("*-deploy.json"));
        Assert.DoesNotContain(StageFiles("*"), file => file.EndsWith("set.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refusal_an_unknown_label_is_refused_as_unknown()
    {
        await DeployedAsync();
        var stage = await StageAsync();

        var stderr = await RefusedAsync(stage, "9.9.9");

        Assert.Equal("jd: promote: release 9.9.9 cannot be promoted to 'stage':\n  unknown label: there is no set.yaml for label 9.9.9 on dev.\n", stderr.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Refusal_a_set_that_is_not_qualified_is_refused_even_when_approved_and_the_approval_is_not_a_reason()
    {
        _live.Answer = _ => [new LiveRevision("app--oob1", 100, ["other-image"])];
        await DeployedAsync();
        var stage = await StageAsync();
        Place(NoApproval, Alice);

        var stderr = await RefusedAsync(stage);

        Assert.Contains("  not qualified on dev: IQ of the newest record (", stderr);
        Assert.DoesNotContain("required approvals", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refusal_every_reason_is_listed_when_the_set_is_neither_qualified_nor_approved()
    {
        _live.Answer = _ => [new LiveRevision("app--oob1", 100, ["other-image"])];
        await DeployedAsync();
        var stage = await StageAsync();

        var stderr = await RefusedAsync(stage);

        Assert.Contains("  not qualified on dev: ", stderr);
        Assert.Contains("  0 of 1 required approvals for 'stage' count.", stderr);
    }

    [Fact]
    public async Task Refusal_too_few_approvals()
    {
        await DeployedAsync();
        var stage = await StageAsync(required: 2, approvers: [Alice, Bob]);
        Place(NoApproval, Alice);

        var stderr = await RefusedAsync(stage);

        Assert.Equal("  1 of 2 required approvals for 'stage' count.", stderr.Split('\n').Single(line => line.Contains("required approvals", StringComparison.Ordinal)).TrimEnd('\r'));
    }

    [Fact]
    public async Task Refusal_the_deployers_own_approval_does_not_count_when_the_environment_separates_them()
    {
        await DeployedAsync(Bob);
        var stage = await StageAsync(approvers: [Bob]);
        Place(NoApproval, Bob);

        var stderr = await RefusedAsync(stage);

        Assert.Contains("  0 of 1 required approvals for 'stage' count.", stderr);
    }

    [Fact]
    public async Task Refusal_an_approval_for_another_set_is_ignored_reported_and_does_not_count()
    {
        await DeployedAsync();
        var stage = await StageAsync();
        Place(NoApproval, Alice, $$"""{ "kind": "ApprovalRecord", "release": { "setSha256": "{{new string('0', 64)}}" } }""");

        var stderr = await RefusedAsync(stage);

        var lines = stderr.ReplaceLineEndings("\n").Split('\n');
        Assert.StartsWith($"jd: promote: ignored {NoApproval}: ", lines[0], StringComparison.Ordinal);
        Assert.Equal<string>(["jd: promote: release 1.0.0 cannot be promoted to 'stage':", "  0 of 1 required approvals for 'stage' count.", string.Empty], lines[1..]);
    }

    [Fact]
    public async Task Refusal_an_environment_without_an_approval_policy_has_nothing_to_promote_through()
    {
        await DeployedAsync();

        var stderr = await RefusedAsync(Environment, identityAsked: 0);

        Assert.Equal("jd: promote: environment 'dev' has no approval policy, so there is nothing to promote through.\n", stderr.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_descriptor_that_cannot_be_loaded_is_exit_1_and_names_the_file()
    {
        await DeployedAsync();
        var broken = Path.Combine(_temp, "broken.env.yaml");
        await File.WriteAllTextAsync(broken, "kind: Nonsense\n");

        var stderr = await RefusedAsync(broken, identityAsked: 0);

        Assert.Contains(broken, stderr);
    }

    [Fact]
    public async Task A_bad_credential_stops_before_the_records_are_read_or_anything_is_deployed()
    {
        var stage = await StageAsync(); // no label was ever deployed: reading the records would say "unknown label"
        _identity.Failure = new InvalidOperationException("ARM_TENANT_ID is not set.");
        var backend = new RecordingBackend();

        var (code, stdout, stderr) = await PromoteAsync(stage, backend);

        Assert.Equal((1, string.Empty, "jd: ARM_TENANT_ID is not set.\n"), (code, stdout, stderr.ReplaceLineEndings("\n")));
        Assert.Empty(backend.Calls);
        Assert.Equal(1, _identity.Calls);
        Assert.Empty(StageFiles("*"));
    }

    [Fact]
    public async Task An_environment_without_an_approval_policy_is_exit_1()
    {
        var (code, _, stderr) = await RunAsync(null, "promote", "1.0.0", "--to", Environment, "--catalog", Catalog);

        Assert.Equal(1, code);
        Assert.Contains("has no approval policy", stderr);
        Assert.Equal(0, _identity.Calls);
    }

    [Fact]
    public async Task The_walk_refuses_a_kept_set_whose_bytes_changed_after_the_check_before_anything_is_deployed_or_kept()
    {
        var stage = await ApprovedAsync();
        var backend = new RecordingBackend();
        var kept = Path.Combine(_records, "dev", "1.0.0", "set.yaml");
        var judged = Sha256(kept);

        var (code, stdout, stderr) = await PromoteChangingTheKeptSetAsync(stage, backend, () => File.AppendAllText(kept, "# changed after the check\n"));

        Assert.Equal(1, code);
        Assert.Equal($"promoting 1.0.0 from dev (set sha256 {judged})\n", stdout.ReplaceLineEndings("\n"));
        Assert.Equal($"jd: promote: the kept set 'dev/1.0.0/set.yaml' changed since it was checked (expected sha256 {judged}).\n", stderr.ReplaceLineEndings("\n"));
        Assert.Empty(backend.Calls);
        Assert.Empty(StageFiles("*-deploy.json"));
        Assert.DoesNotContain(StageFiles("*"), file => file.EndsWith("set.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_kept_set_that_cannot_be_read_by_the_walk_exits_2_naming_it_before_anything_is_deployed_or_kept()
    {
        var stage = await ApprovedAsync();
        var backend = new RecordingBackend();
        var kept = Path.Combine(_records, "dev", "1.0.0", "set.yaml");
        var judged = Sha256(kept);

        var (code, stdout, stderr) = await PromoteChangingTheKeptSetAsync(stage, backend, () => File.Delete(kept));

        Assert.Equal(2, code);
        Assert.Equal($"promoting 1.0.0 from dev (set sha256 {judged})\n", stdout.ReplaceLineEndings("\n"));
        Assert.Equal($"jd: Could not find file '{kept}'.\n", stderr.ReplaceLineEndings("\n"));
        Assert.Empty(backend.Calls);
        Assert.Empty(StageFiles("*-deploy.json"));
        Assert.DoesNotContain(StageFiles("*"), file => file.EndsWith("set.yaml", StringComparison.Ordinal));
    }

    // A promotion whose kept set change() alters between promote's check and its walk: as `promoting` is printed, after the one and before the other.
    private async Task<(int Code, string Out, string Err)> PromoteChangingTheKeptSetAsync(string stage, RecordingBackend backend, Action change)
    {
        var stdout = new OnLine(line =>
        {
            if (line.StartsWith("promoting ", StringComparison.Ordinal))
            {
                change();
            }
        });
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(["promote", "1.0.0", "--to", stage, "--catalog", Catalog], stdout, stderr, CancellationToken.None, Connections(backend));
        return (code, stdout.ToString(), stderr.ToString());
    }

    // Collects what is written, and tells act each line as it is written.
    private sealed class OnLine(Action<string> act) : StringWriter(CultureInfo.InvariantCulture)
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            act(value ?? string.Empty);
        }
    }

    [Theory]
    [InlineData("expected exactly one release label.", "promote")]
    [InlineData("expected exactly one release label.", "promote", "1.0.0", "2.0.0", "--to", "s.yaml", "--catalog", "c")]
    [InlineData("promote needs --to and --catalog.", "promote", "1.0.0", "--catalog", "c")]
    [InlineData("promote needs --to and --catalog.", "promote", "1.0.0", "--to", "s.yaml")]
    [InlineData("--to needs a value.", "promote", "1.0.0", "--catalog", "c", "--to")]
    [InlineData("unknown option '--env'.", "promote", "1.0.0", "--to", "s.yaml", "--catalog", "c", "--env", "e.yaml")]
    public async Task Usage_errors_exit_2_print_the_problem_and_the_usage_and_touch_no_identity_backend_or_record(string problem, params string[] args)
    {
        var backend = new RecordingBackend();

        AssertUsageError(await RunAsync(backend, args), problem);
        Assert.Equal(0, _identity.Calls);
        Assert.Empty(backend.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    // Security boundary: a label that cannot name a release is refused before the identity, the records or the backend is reached.
    [Theory]
    [InlineData("foo", "'foo' is not a release label (like 1.0.0 or 1.0.0-rc.1).")]
    [InlineData("../x", "'../x' cannot name a directory of the release records: it is empty or contains '..', '/' or '\\'.")]
    public async Task A_label_that_cannot_name_a_release_is_a_usage_error_before_the_identity_or_any_record(string label, string problem)
    {
        var stage = await StageAsync();
        var backend = new RecordingBackend();

        AssertUsageError(await RunAsync(backend, "promote", label, "--to", stage, "--catalog", Catalog), problem);
        Assert.Equal(0, _identity.Calls);
        Assert.Empty(backend.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    [Fact]
    public async Task A_descriptor_or_catalog_that_does_not_exist_is_exit_2_before_any_identity_or_record()
    {
        var stage = await StageAsync();

        var missingDescriptor = await RunAsync(null, "promote", "1.0.0", "--to", Path.Combine(_temp, "nope.yaml"), "--catalog", Catalog);
        var missingCatalog = await RunAsync(null, "promote", "1.0.0", "--to", stage, "--catalog", Path.Combine(_temp, "nope"));

        Assert.Equal((2, string.Empty, $"jd: cannot read '{Path.Combine(_temp, "nope.yaml")}': not found or not a file.\n"), (missingDescriptor.Code, missingDescriptor.Out, missingDescriptor.Err.ReplaceLineEndings("\n")));
        Assert.Equal((2, string.Empty, $"jd: cannot read '{Path.Combine(_temp, "nope")}': not found or not a directory.\n"), (missingCatalog.Code, missingCatalog.Out, missingCatalog.Err.ReplaceLineEndings("\n")));
        Assert.Equal(0, _identity.Calls);
    }

    // Edge 1 (docs/architecture/application.md section 8): the label is the use case's to check, after the command has its options and its inputs
    // exist, so a missing --to or an unreadable one is reported first; before, the label was.
    [Fact]
    public async Task A_missing_or_unreadable_target_is_reported_before_a_label_that_cannot_name_a_release()
    {
        var missing = Path.Combine(_temp, "nope.yaml");

        var unreadable = await RunAsync(null, "promote", "../x", "--to", missing, "--catalog", Catalog);
        var noTarget = await RunAsync(null, "promote", "../x", "--catalog", Catalog);

        Assert.Equal((2, string.Empty, $"jd: cannot read '{missing}': not found or not a file.\n"), (unreadable.Code, unreadable.Out, unreadable.Err.ReplaceLineEndings("\n")));
        AssertUsageError(noTarget, "promote needs --to and --catalog.");
        Assert.Equal(0, _identity.Calls);
    }

    // Edge 2 (docs/architecture/application.md section 8): an I/O failure that escapes approve or promote before its walk (here: the records cannot be
    // reached) is exit 2 with its message, as for every other command; before, it escaped jd as an unhandled exception.
    [Theory]
    [InlineData("promote", "1.0.0", "--to", StagePath, "--catalog", CatalogPath)]
    [InlineData("approve", "1.0.0", "--env", StagePath, "--meaning", "ready")]
    public async Task An_io_failure_before_the_walk_is_exit_2_with_its_message(params string[] line)
    {
        await DeployedAsync();
        var stage = await StageAsync();
        Act(Alice);
        var args = line.Select(arg => arg switch { StagePath => stage, CatalogPath => Catalog, _ => arg }).ToArray();

        var io = await RunWithAsync(Connections(null) with { Records = () => throw new IOException("the records cannot be read") }, args);
        var access = await RunWithAsync(Connections(null) with { Records = () => throw new UnauthorizedAccessException("the records are not ours") }, args);

        Assert.Equal((2, string.Empty, "jd: the records cannot be read\n"), (io.Code, io.Out, io.Err.ReplaceLineEndings("\n")));
        Assert.Equal((2, string.Empty, "jd: the records are not ours\n"), (access.Code, access.Out, access.Err.ReplaceLineEndings("\n")));
        Assert.Empty(StageFiles("*"));
    }

    // The edge of the catalog read (docs/architecture/application.md section 8): the CLI reads the catalog before the use case, so a catalog that exists but
    // cannot be read is exit 2 with .NET's message before the identity is asked or the approvals are checked; before, it was read only at the walk.
    [Fact]
    public async Task A_catalog_that_exists_but_cannot_be_read_is_exit_2_before_the_identity_or_the_approvals()
    {
        await DeployedAsync();
        var stage = await StageAsync();
        var catalog = Path.Combine(_temp, "catalog");
        CliTests.CopyDirectory(Catalog, catalog);
        var unreadable = Path.Combine(catalog, "naming.yaml");
        if (!TryRestrict(unreadable))
        {
            return;
        }

        var identityCalls = _identity.Calls;

        var (code, stdout, stderr) = await RunAsync(null, "promote", "1.0.0", "--to", stage, "--catalog", catalog);

        Assert.Equal((2, string.Empty, $"jd: Access to the path '{unreadable}' is denied.\n"), (code, stdout, stderr.ReplaceLineEndings("\n")));
        Assert.Equal(identityCalls, _identity.Calls);
        Assert.Empty(StageFiles("*"));
    }
}
