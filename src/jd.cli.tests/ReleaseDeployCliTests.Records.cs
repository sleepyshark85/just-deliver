using System.Security.Cryptography;
using jd.core.identity;
using jd.core.revisions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.cli.tests;

// A principal that answers without Entra; a test can make it fail instead.
internal sealed class FakeIdentity : IDeployerIdentity
{
    public DeployerIdentity Value { get; set; } = new("tenant-1", "client-1", "object-1");
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }

    public Task<DeployerIdentity> GetAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Failure is null ? Task.FromResult(Value) : Task.FromException<DeployerIdentity>(Failure);
    }
}

// The revisions of every app as the platform would report them: by default one, holding all traffic, running the sample's pinned image.
internal sealed class FakeLiveRevisions : ILiveRevisions
{
    public const string Pinned = "ghcr.io/sleepyshark85/just-deliver-sample-app@sha256:267b1385d6102da0d5693506e740d509a40b654e6daa2a99c1169938d182cab9";

    public Func<string, IReadOnlyList<LiveRevision>> Answer { get; set; } = _ => [new LiveRevision("app--r1", 100, [Pinned])];
    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<LiveRevision>> GetAsync(string resourceId, CancellationToken cancellationToken)
    {
        Calls.Add(resourceId);
        return Task.FromResult(Answer(resourceId));
    }
}

// A clock that moves one second at every reading of the time, so every record gets its own time. Its timestamps, which time a stretch of work,
// stand still until a test moves them with Advance (the fake backend does, for each call it answers).
internal sealed class StepClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan time) => _timestamp += time.Ticks;

    public override DateTimeOffset GetUtcNow()
    {
        var now = _now;
        _now = _now.AddSeconds(1);
        return now;
    }
}

// The release record of `jd release deploy` (docs/architecture/release-record.md): where it goes, what it names, and when nothing is written.
public sealed partial class ReleaseDeployCliTests
{
    private string LabelDirectory => Path.Combine(_records, "dev", "1.0.0");

    private string[] RecordFiles => Directory.Exists(LabelDirectory) ? Directory.GetFiles(LabelDirectory, "*-deploy.json").Order(StringComparer.Ordinal).ToArray() : [];

    // Dates stay text: the record spells them as the schema says.
    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task A_deploy_writes_one_record_naming_the_principal_the_set_and_the_oq_of_each_workload()
    {
        var set = await CreateSetAsync();
        _identity.Value = new DeployerIdentity("the-tenant", "the-client", "the-object");

        var (code, stdout, _) = await RunAsync(new RecordingBackend(), Deploy(set));

        Assert.Equal(0, code);
        var file = Assert.Single(RecordFiles);
        Assert.Contains($"record: {file}", stdout);
        Assert.EndsWith("-deploy.json", file, StringComparison.Ordinal);
        var record = Read(file);
        Assert.Equal("succeeded", (string?)record["outcome"]);
        Assert.Equal(new JObject { ["tenantId"] = "the-tenant", ["clientId"] = "the-client", ["objectId"] = "the-object" }.ToString(), record["identity"]?.ToString());
        Assert.Equal([Worker, App], record["workloads"]!.Select(w => (string?)w["name"]));
        Assert.All(record["workloads"]!, w =>
        {
            Assert.Equal("passed", (string?)w["oq"]!["status"]);
            Assert.Equal("/health", (string?)w["oq"]!["contract"]!["path"]);
            Assert.Equal("HTTP 200", (string?)w["oq"]!["detail"]);
            Assert.Equal(1, (int?)w["oq"]!["attempts"]);
        });
        Assert.NotNull(record["deploySeconds"]);
        // The clock moves a second at each reading: the run read it when it started and when it finished.
        Assert.Equal("2026-10-17T08:00:00Z", (string?)record["startedAt"]);
        Assert.Equal("2026-10-17T08:00:01Z", (string?)record["finishedAt"]);
        Assert.Equal("20261017T080001000Z-deploy.json", Path.GetFileName(file));
    }

    [Fact]
    public async Task The_set_hash_in_the_record_is_the_hash_of_set_yaml_which_holds_the_sets_exact_bytes()
    {
        var set = await CreateSetAsync();

        await RunAsync(new RecordingBackend(), Deploy(set));

        var kept = Path.Combine(LabelDirectory, "set.yaml");
        Assert.Equal(await File.ReadAllBytesAsync(set), await File.ReadAllBytesAsync(kept));
        Assert.Equal(Sha256(kept), (string?)Read(Assert.Single(RecordFiles))["release"]!["setSha256"]);
    }

    [Fact]
    public async Task A_rerun_adds_a_record_reuses_set_yaml_and_leaves_the_old_bytes_unchanged()
    {
        var set = await CreateSetAsync();
        await RunAsync(new RecordingBackend(), Deploy(set));
        var first = Assert.Single(RecordFiles);
        var firstBytes = await File.ReadAllBytesAsync(first);
        var kept = Path.Combine(LabelDirectory, "set.yaml");
        var keptBytes = await File.ReadAllBytesAsync(kept);

        var (code, _, stderr) = await RunAsync(new RecordingBackend(), Deploy(set));

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.Equal(2, RecordFiles.Length);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(first));
        Assert.Equal(keptBytes, await File.ReadAllBytesAsync(kept));
    }

    [Fact]
    public async Task A_different_set_under_the_same_label_is_refused_before_the_backend_is_called_and_set_yaml_is_never_overwritten()
    {
        var set = await CreateSetAsync();
        await RunAsync(new RecordingBackend(), Deploy(set));
        var kept = Path.Combine(LabelDirectory, "set.yaml");
        var keptBytes = await File.ReadAllBytesAsync(kept);
        var other = await CreateSetAsync(app => app.Replace("LOG_LEVEL: info", "LOG_LEVEL: debug"), "other.yaml");
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Deploy(other));

        Assert.Equal(1, code);
        Assert.Empty(backend.Calls);
        Assert.Contains($"'{kept}' holds a different release set for label 1.0.0 on dev", stderr);
        Assert.Equal(keptBytes, await File.ReadAllBytesAsync(kept));
        Assert.Single(RecordFiles);
    }

    [Fact]
    public async Task A_failed_run_writes_a_failed_record_naming_the_failing_node_and_the_workloads_not_started()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend { FailOn = $"{Worker}.dev.database.container" };

        var (code, _, _) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        var record = Read(Assert.Single(RecordFiles));
        Assert.Equal("failed", (string?)record["outcome"]);
        Assert.Equal(Worker, (string?)record["failure"]!["workload"]);
        Assert.Equal($"{Worker}/dev/database/container", (string?)record["failure"]!["node"]);
        Assert.Equal([App], record["notStarted"]!.Select(n => (string?)n));
        var worker = record["workloads"]![0]!;
        var app = record["workloads"]![1]!;
        Assert.NotNull(worker["run"]);
        Assert.Null(app["run"]);
        Assert.Equal("notRun", (string?)worker["oq"]!["status"]);
        Assert.Equal("notRun", (string?)app["oq"]!["status"]);
    }

    [Fact]
    public async Task A_preview_a_usage_error_and_a_resolution_error_write_nothing()
    {
        var set = await CreateSetAsync();
        var broken = await CreateSetAsync(app => app.Replace("- type: database", "- type: no-such-type\n    id: database"), "broken.yaml");
        var backend = new RecordingBackend();

        _recordsVariable = null; // a preview needs no records directory
        var (previewCode, _, _) = await RunAsync(backend, Deploy(set, "--preview"));
        _recordsVariable = _records; // and writes nothing where one is set
        var (recordedPreviewCode, _, _) = await RunAsync(backend, Deploy(set, "--preview"));
        var (usageCode, _, _) = await RunAsync(backend, "release", "deploy");
        var (resolutionCode, _, _) = await RunAsync(backend, Deploy(broken));

        Assert.Equal(0, previewCode);
        Assert.Equal(0, recordedPreviewCode);
        Assert.Equal(2, usageCode);
        Assert.Equal(1, resolutionCode);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
        Assert.Equal(0, _identity.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("absent")]
    public async Task Without_an_existing_records_directory_it_exits_2_before_the_backend_or_the_principal_is_asked(string? variable)
    {
        var set = await CreateSetAsync();
        _recordsVariable = variable is { Length: > 0 } ? Path.Combine(_temp, variable) : variable;
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(2, code);
        Assert.Contains("JD_RECORDS_DIR must name an existing directory", stderr);
        Assert.Empty(backend.Calls);
        Assert.Equal(0, _identity.Calls);
    }

    [Fact]
    public async Task A_principal_that_cannot_be_named_stops_the_run_before_the_backend_is_called_and_creates_no_set_yaml()
    {
        var set = await CreateSetAsync();
        _identity.Failure = new InvalidOperationException("Azure did not accept the credential in ARM_TENANT_ID.");
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.Contains("jd: Azure did not accept the credential in ARM_TENANT_ID.", stderr);
        Assert.Empty(backend.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    [Fact]
    public async Task Without_the_arm_variables_the_run_stops_naming_them_before_the_backend_is_called_and_creates_no_set_yaml()
    {
        var set = await CreateSetAsync();
        _port = null; // the composition root builds the identity from the ARM_* variables, which this run does not have
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.Contains("ARM_TENANT_ID, ARM_CLIENT_ID, ARM_CLIENT_SECRET", stderr);
        Assert.Empty(backend.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    [Fact]
    public async Task A_malformed_tenant_id_stops_the_run_with_a_clear_error_before_the_backend_is_called_and_never_prints_the_secret()
    {
        var set = await CreateSetAsync();
        _port = null;
        (_variables["ARM_TENANT_ID"], _variables["ARM_CLIENT_ID"], _variables["ARM_CLIENT_SECRET"]) = ("not a tenant!", "client", "the-secret-value");
        var backend = new RecordingBackend();

        var (code, stdout, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.Contains("ARM_TENANT_ID, ARM_CLIENT_ID and ARM_CLIENT_SECRET do not make a valid credential", stderr);
        Assert.DoesNotContain("the-secret-value", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
        Assert.Empty(backend.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    [Fact]
    public async Task A_label_the_store_cannot_use_is_refused_with_a_message_before_the_backend_is_called_and_creates_nothing()
    {
        var set = await CreateSetAsync(label: "1.0.0-rc..1");
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.Contains("jd: '1.0.0-rc..1' cannot name a directory of the release records", stderr);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
        Assert.Empty(backend.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    [Fact]
    public async Task A_record_that_cannot_be_written_exits_1_and_says_the_release_was_deployed()
    {
        var set = await CreateSetAsync();
        await RunAsync(new RecordingBackend(), Deploy(set));
        var first = Assert.Single(RecordFiles);
        var firstBytes = await File.ReadAllBytesAsync(first);
        _clock = new StepClock(); // the same readings again: the second record's path is the first one's

        var backend = new RecordingBackend();
        var (code, _, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.NotEmpty(backend.Calls);
        Assert.Contains("jd: the release was deployed, but its record could not be written", stderr);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(first));
        Assert.Single(RecordFiles);
    }
}
