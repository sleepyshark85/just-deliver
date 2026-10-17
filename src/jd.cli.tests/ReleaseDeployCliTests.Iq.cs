using jd.core.revisions;
using Xunit;

namespace jd.cli.tests;

// IQ inside `jd release deploy` (docs/architecture/release-record.md section 7): it runs after every workload deployed, never before or after a failure,
// and its verdict is part of the deploy's one record.
public sealed partial class ReleaseDeployCliTests
{
    private static bool Previews(string call) => call.StartsWith("preview ", StringComparison.Ordinal);

    [Fact]
    public async Task Iq_runs_after_every_workload_deployed_never_between_them()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend();

        var (code, _, _) = await RunAsync(backend, Deploy(set));

        Assert.Equal(0, code);
        var firstPreview = backend.Calls.FindIndex(Previews);
        Assert.True(firstPreview > 0);
        Assert.DoesNotContain(backend.Calls.Take(firstPreview), Previews);
        Assert.DoesNotContain(backend.Calls.Skip(firstPreview), c => !Previews(c));
        Assert.Equal(2, _live.Calls.Count); // one app per workload
    }

    [Fact]
    public async Task A_passing_iq_exits_0_and_the_one_deploy_record_says_succeeded_and_passed_with_every_check()
    {
        var set = await CreateSetAsync();
        _variables["ARM_CLIENT_SECRET"] = "the-secret-value";

        var (code, stdout, stderr) = await RunAsync(new RecordingBackend(), Deploy(set));

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.Contains("release 1.0.0 on dev: IQ passed", stdout);
        var file = Assert.Single(RecordFiles);
        Assert.Empty(QualifyRecordFiles);
        var record = Read(file);
        Assert.Equal("succeeded", (string?)record["outcome"]);
        Assert.Equal("passed", (string?)record["iq"]!["status"]);
        Assert.NotEmpty(record["iq"]!["checks"]!);
        Assert.All(record["iq"]!["checks"]!, c => Assert.Equal("passed", (string?)c["status"]));
        Assert.DoesNotContain("the-secret-value", File.ReadAllText(file) + stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_workload_runs_no_iq_and_the_record_says_failed_and_notRun()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend { FailOn = $"{Worker}.dev._workload.appinsights" };

        var (code, stdout, _) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.DoesNotContain(backend.Calls, Previews);
        Assert.Empty(_live.Calls);
        Assert.DoesNotContain(" IQ ", stdout, StringComparison.Ordinal); // the IQ verdict line; "IQ" alone can occur in the random temp path of the record
        var record = Read(Assert.Single(RecordFiles));
        Assert.Equal("failed", (string?)record["outcome"]);
        Assert.Equal("notRun", (string?)record["iq"]!["status"]);
        Assert.Empty(record["iq"]!["checks"]!);
    }

    [Fact]
    public async Task A_failed_iq_after_a_successful_deploy_exits_1_lists_every_failed_check_and_the_record_says_succeeded_and_failed()
    {
        var set = await CreateSetAsync();
        _live.Answer = _ => [new LiveRevision("app--oob1", 100, [Other])];
        var backend = new RecordingBackend();
        backend.PreviewDrift.Add($"{Worker}.dev.database.container");

        var (code, stdout, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.Contains($"jd: IQ failed: {Worker}: node {Worker}/dev/database/container differs from what is deployed: Update 1", stderr);
        Assert.Contains($"jd: IQ failed: {Worker}: pinned {FakeLiveRevisions.Pinned}, live {Other} on revision app--oob1", stderr);
        Assert.Contains($"jd: IQ failed: {App}: pinned {FakeLiveRevisions.Pinned}, live {Other} on revision app--oob1", stderr);
        Assert.Contains("release 1.0.0 on dev: 2 deployed, 0 unchanged, 0 failed, 0 not started.", stdout);
        Assert.Contains("IQ failed, ", stdout);
        var file = Assert.Single(RecordFiles);
        Assert.Contains($"record: {file}", stdout);
        var record = Read(file);
        Assert.Equal("succeeded", (string?)record["outcome"]);
        Assert.Equal("failed", (string?)record["iq"]!["status"]);
        Assert.Equal(3, record["iq"]!["checks"]!.Count(c => (string?)c["status"] == "failed"));
        Assert.Equal(14, backend.Calls.Count(c => !Previews(c))); // nothing is rolled back or redone
    }

    [Fact]
    public async Task The_deploy_time_is_the_walk_alone_and_iq_has_its_own_time()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend { Clock = _clock, DeployTime = TimeSpan.FromSeconds(2), PreviewTime = TimeSpan.FromSeconds(5) };

        var (code, _, _) = await RunAsync(backend, Deploy(set));

        Assert.Equal(0, code);
        var record = Read(Assert.Single(RecordFiles));
        Assert.Equal(2 * backend.Calls.Count(c => c.StartsWith("deploy ", StringComparison.Ordinal)), (double)record["deploySeconds"]!);
        Assert.Equal(5 * backend.Calls.Count(Previews), (double)record["iq"]!["elapsedSeconds"]!);
    }

    [Fact]
    public async Task A_reader_of_live_revisions_that_cannot_be_built_stops_the_run_before_anything_is_deployed()
    {
        var set = await CreateSetAsync();
        _reader = null; // built from the ARM_* variables, which this run does not have
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.Contains("ARM_TENANT_ID, ARM_CLIENT_ID, ARM_CLIENT_SECRET", stderr);
        Assert.Empty(backend.Calls);
        Assert.Empty(RecordFiles);
    }
}
