using jd.core.revisions;
using Xunit;

namespace jd.cli.tests;

// `jd release qualify` (docs/architecture/release-record.md sections 5 to 7): the deployment is checked, never changed, and the check is recorded.
public sealed partial class ReleaseDeployCliTests
{
    private const string Other = "mcr.microsoft.com/k8se/quickstart:latest";

    private string[] QualifyRecordFiles => Directory.Exists(LabelDirectory) ? Directory.GetFiles(LabelDirectory, "*-qualify.json").Order(StringComparer.Ordinal).ToArray() : [];

    private static string[] Qualify(string set, params string[] more) => ["release", "qualify", set, "--env", Environment, "--catalog", Catalog, .. more];

    [Fact]
    public async Task A_qualification_of_a_clean_deployment_exits_0_deploys_nothing_creates_set_yaml_and_writes_a_qualify_record()
    {
        var set = await CreateSetAsync();
        _variables["ARM_CLIENT_SECRET"] = "the-secret-value";
        var backend = new RecordingBackend { Deployed = true };

        var (code, stdout, stderr) = await RunAsync(backend, Qualify(set));

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.DoesNotContain(backend.Calls, c => c.StartsWith("deploy ", StringComparison.Ordinal));
        Assert.DoesNotContain(backend.Calls, c => c.StartsWith("probe ", StringComparison.Ordinal));
        Assert.Equal(await File.ReadAllBytesAsync(set), await File.ReadAllBytesAsync(Path.Combine(LabelDirectory, "set.yaml")));
        var file = Assert.Single(QualifyRecordFiles);
        Assert.Contains($"record: {file}", stdout);
        Assert.Contains("release 1.0.0 on dev: IQ passed", stdout);
        var record = Read(file);
        Assert.Equal("qualify", (string?)record["activity"]);
        Assert.Equal("passed", (string?)record["iq"]!["status"]);
        Assert.All(record["iq"]!["checks"]!, c => Assert.Equal("passed", (string?)c["status"]));
        Assert.Equal([Worker, App], record["workloads"]!.Select(w => (string?)w["name"]));
        Assert.All(record["workloads"]!, w => Assert.Null(w["run"]));
        Assert.Null(record["deploySeconds"]);
        Assert.Equal(Sha256(Path.Combine(LabelDirectory, "set.yaml")), (string?)record["release"]!["setSha256"]);
        Assert.Equal(2, _live.Calls.Count);
        Assert.DoesNotContain("the-secret-value", File.ReadAllText(file) + stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_qualification_checks_the_set_yaml_of_an_earlier_deploy_and_adds_a_record_beside_it()
    {
        var set = await CreateSetAsync();
        await RunAsync(new RecordingBackend(), Deploy(set));
        var kept = Path.Combine(LabelDirectory, "set.yaml");
        var keptBytes = await File.ReadAllBytesAsync(kept);

        var (code, _, stderr) = await RunAsync(new RecordingBackend { Deployed = true }, Qualify(set));

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.Single(RecordFiles);
        Assert.Single(QualifyRecordFiles);
        Assert.Equal(keptBytes, await File.ReadAllBytesAsync(kept));
    }

    [Fact]
    public async Task A_different_set_under_the_label_is_refused_before_any_backend_call_or_live_read()
    {
        var set = await CreateSetAsync();
        await RunAsync(new RecordingBackend(), Deploy(set));
        var other = await CreateSetAsync(app => app.Replace("LOG_LEVEL: info", "LOG_LEVEL: debug"), "other.yaml");
        var backend = new RecordingBackend { Deployed = true };
        _live.Calls.Clear(); // the deploy above qualified itself

        var (code, _, stderr) = await RunAsync(backend, Qualify(other));

        Assert.Equal(1, code);
        Assert.Contains("holds a different release set for label 1.0.0 on dev", stderr);
        Assert.Empty(backend.Calls);
        Assert.Empty(_live.Calls);
        Assert.Empty(QualifyRecordFiles);
    }

    [Fact]
    public async Task A_running_image_other_than_the_pinned_one_exits_1_listing_the_check_and_the_record_says_failed()
    {
        var set = await CreateSetAsync();
        _live.Answer = id => [new LiveRevision("app--r1", 0, [FakeLiveRevisions.Pinned]), new LiveRevision("app--oob1", 100, [Other])];
        var backend = new RecordingBackend { Deployed = true };

        var (code, stdout, stderr) = await RunAsync(backend, Qualify(set));

        Assert.Equal(1, code);
        Assert.Contains($"jd: IQ failed: {Worker}: pinned {FakeLiveRevisions.Pinned}, live {Other} on revision app--oob1", stderr);
        Assert.Contains($"jd: IQ failed: {App}: pinned {FakeLiveRevisions.Pinned}, live {Other} on revision app--oob1", stderr);
        Assert.Contains("IQ failed, ", stdout);
        var record = Read(Assert.Single(QualifyRecordFiles));
        Assert.Equal("failed", (string?)record["iq"]!["status"]);
        Assert.Equal(2, record["iq"]!["checks"]!.Count(c => (string?)c["status"] == "failed"));
        Assert.DoesNotContain(backend.Calls, c => c.StartsWith("deploy ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_release_that_was_never_deployed_fails_every_check_it_can_and_does_not_crash()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend(); // nothing deployed: every preview would create, and no runtime is in state

        var (code, _, stderr) = await RunAsync(backend, Qualify(set));

        Assert.Equal(1, code);
        Assert.Contains($"node {Worker}/dev/database/container differs from what is deployed: Create 1", stderr);
        Assert.Contains($"node {Worker}/dev/@workload/runtime cannot be previewed (PendingUpstream)", stderr);
        Assert.Contains($"{Worker}: node {Worker}/dev/@workload/runtime has no 'containerAppId' in its state", stderr);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
        Assert.Single(QualifyRecordFiles);
    }

    [Fact]
    public async Task A_reader_error_is_a_failed_check_with_its_reason()
    {
        var set = await CreateSetAsync();
        _live.Answer = _ => throw new InvalidOperationException("Azure refused to list the revisions of 'x': HTTP 404, ResourceNotFound.");

        var (code, _, stderr) = await RunAsync(new RecordingBackend { Deployed = true }, Qualify(set));

        Assert.Equal(1, code);
        Assert.Contains($"jd: IQ failed: {Worker}: the live revisions could not be checked: Azure refused to list the revisions of 'x': HTTP 404, ResourceNotFound.", stderr);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("absent")]
    public async Task Without_an_existing_records_directory_a_qualification_exits_2_before_the_backend_or_the_principal_is_asked(string? variable)
    {
        var set = await CreateSetAsync();
        _recordsVariable = variable is null ? null : Path.Combine(_temp, variable);
        var backend = new RecordingBackend { Deployed = true };

        var (code, _, stderr) = await RunAsync(backend, Qualify(set));

        Assert.Equal(2, code);
        Assert.Contains("JD_RECORDS_DIR must name an existing directory", stderr);
        Assert.Empty(backend.Calls);
        Assert.Equal(0, _identity.Calls);
        Assert.Empty(_live.Calls);
    }

    [Fact]
    public async Task A_principal_that_cannot_be_named_stops_a_qualification_before_any_backend_call()
    {
        var set = await CreateSetAsync();
        _identity.Failure = new InvalidOperationException("Azure did not accept the credential in ARM_TENANT_ID.");
        var backend = new RecordingBackend { Deployed = true };

        var (code, _, stderr) = await RunAsync(backend, Qualify(set));

        Assert.Equal(1, code);
        Assert.Contains("jd: Azure did not accept the credential in ARM_TENANT_ID.", stderr);
        Assert.Empty(backend.Calls);
        Assert.Empty(_live.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    [Fact]
    public async Task A_reader_of_live_revisions_that_cannot_be_built_stops_a_qualification_before_any_backend_call()
    {
        var set = await CreateSetAsync();
        _reader = null; // built from the ARM_* variables, which this run does not have
        var backend = new RecordingBackend { Deployed = true };

        var (code, _, stderr) = await RunAsync(backend, Qualify(set));

        Assert.Equal(1, code);
        Assert.Contains("ARM_TENANT_ID, ARM_CLIENT_ID, ARM_CLIENT_SECRET", stderr);
        Assert.Empty(backend.Calls);
        Assert.Empty(QualifyRecordFiles);
    }

    [Theory]
    [InlineData("release", "qualify")]
    [InlineData("release", "qualify", "s.yaml", "--catalog", "c")]
    [InlineData("release", "qualify", "s.yaml", "--env", "e.yaml", "--catalog", "c", "--preview")]
    [InlineData("release", "qualify", "s.yaml", "--env", "e.yaml", "--catalog", "c", "--json")]
    public async Task Usage_errors_of_qualify_exit_2_and_print_the_usage(params string[] args)
    {
        var (code, _, stderr) = await RunAsync(new RecordingBackend(), args);

        Assert.Equal(2, code);
        Assert.Contains("Usage:", stderr);
    }

    [Fact]
    public async Task Help_lists_qualify()
    {
        var (_, stdout, _) = await RunAsync(null, "--help");

        Assert.Contains("jd release qualify <release-set.yaml> --env <environment.yaml> --catalog <dir>", stdout);
    }
}
