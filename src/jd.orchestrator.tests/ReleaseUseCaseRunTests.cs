using jd.core.identity;
using jd.core.records;
using jd.core.revisions;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.tests;
using Xunit;
using static jd.orchestrator.tests.WorkloadUseCaseTests;

namespace jd.orchestrator.tests;

// DeployRelease and QualifyRelease after their start, over documents and fakes only: the walk's progress, IQ only after every workload deployed, the
// record written whether IQ passed or not, and what each use case decides (docs/architecture/application.md section 3).
public sealed class ReleaseUseCaseRunTests
{
    internal const string Image = "registry.example/shop@sha256:0000000000000000000000000000000000000000000000000000000000000000";
    private const string FirstNode = "shop.dev.thing.group";

    // A deploy reports a change, and a preview of what it deployed none, so IQ passes unless a test says otherwise.
    private readonly FakeBackend _backend = new() { DetectChanges = true, Summary = new() { ["Same"] = 1 } };
    private readonly MemoryRecordStore _records = new();
    private readonly FakeLiveRevisions _live = new(_ => [new LiveRevision("app--1", 100, [Image])]);
    private IRecordStore? _store;
    private int _qualifiers;

    private sealed class FakeIdentity : IDeployerIdentity
    {
        public Task<DeployerIdentity> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new DeployerIdentity("tenant-1", "client-1", "object-1"));
    }

    // Records that keep the set but refuse every record, as a full disk or a read-only mount would.
    private sealed class RefusingRecords(IRecordStore records, Exception refusal) : IRecordStore
    {
        public Task<string> CreateAsync(string environment, string label, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            name == "set.yaml" ? records.CreateAsync(environment, label, name, content, cancellationToken) : throw refusal;

        public Task<IReadOnlyList<string>> ListAsync(string environment, string label, CancellationToken cancellationToken) => records.ListAsync(environment, label, cancellationToken);

        public Task<byte[]> ReadAsync(string environment, string label, string name, CancellationToken cancellationToken) => records.ReadAsync(environment, label, name, cancellationToken);
    }

    // The live revisions factory is built only beside a qualifier, so counting it counts the qualifiers built.
    private Connections Connections() => new(
        () => _store ?? _records,
        () => new FakeIdentity(),
        () =>
        {
            _qualifiers++;
            return _live;
        },
        () => _backend,
        () => new FakeProbe(_backend.Log),
        () => TimeProvider.System,
        part => new RecordingConnections(_backend).With().Templates(part));

    private static async Task<SourceDocument> SetAsync() => new("r.set.yaml", await ReleaseStartTests.SetAsync());

    private async Task<DeployReleaseResult> DeployAsync(bool preview = false, List<Progress>? progress = null) =>
        await DeployRelease.RunAsync(
            new DeployReleaseInput(await SetAsync(), Document("envs/dev.yaml", WorkloadUseCaseTests.Environment), Catalog, preview), Connections(), progress is null ? null : progress.Add, CancellationToken.None);

    private async Task<QualifyReleaseResult> QualifyAsync() =>
        await QualifyRelease.RunAsync(new QualifyReleaseInput(await SetAsync(), Document("envs/dev.yaml", WorkloadUseCaseTests.Environment), Catalog), Connections(), CancellationToken.None);

    private IEnumerable<string> RecordNames(string activity) => _records.Files.Keys.Where(k => k.EndsWith($"-{activity}.json", StringComparison.Ordinal));

    [Fact]
    public async Task A_deploy_reports_each_workload_before_its_nodes_then_returns_the_summary_the_iq_and_the_record()
    {
        var progress = new List<Progress>();

        var result = await DeployAsync(progress: progress);

        Assert.Equal(new WorkloadStarted("shop"), progress[0]);
        Assert.Equal(["shop/dev/thing/group", "shop/dev/@workload/runtime"], progress.Skip(1).Select(p => Assert.IsType<NodeFinished>(p).Report.NodeId));
        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Null(result.Problem);
        Assert.Equal(("1.0.0", "dev"), (result.Set?.Label, result.Environment?.Name));
        var summary = Assert.IsType<ReleaseSummary>(result.Summary);
        Assert.Equal((1, 0, 0, 0), (summary.Deployed, summary.Unchanged, summary.Previewed, summary.Failed));
        Assert.Empty(summary.NotStarted);
        Assert.True(result.Iq?.Passed);
        Assert.Equal(Assert.Single(RecordNames("deploy")), result.Record);
    }

    [Fact]
    public async Task A_preview_counts_its_workloads_as_previewed_records_nothing_and_builds_no_qualifier()
    {
        var result = await DeployAsync(preview: true);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Equal((0, 0, 1, 0), (result.Summary?.Deployed, result.Summary?.Unchanged, result.Summary?.Previewed, result.Summary?.Failed));
        Assert.Null(result.Iq);
        Assert.Null(result.Record);
        Assert.Empty(_records.Files);
        Assert.Equal(0, _qualifiers);
        Assert.DoesNotContain(_backend.Calls, c => c.Op == "deploy");
    }

    [Fact]
    public async Task A_preview_that_fails_fails_and_records_nothing()
    {
        _backend.PreviewFailOn = FirstNode;

        var result = await DeployAsync(preview: true);

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal((0, 1), (result.Summary?.Previewed, result.Summary?.Failed));
        Assert.Empty(_records.Files);
    }

    // Security boundary: IQ checks a deployment only once every workload of it deployed.
    [Fact]
    public async Task A_failed_workload_runs_no_iq_and_is_recorded_as_failed()
    {
        _backend.FailOn = FirstNode;

        var result = await DeployAsync();

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Null(result.Iq);
        Assert.Empty(_live.Calls);
        Assert.DoesNotContain(_backend.Calls, c => c.Op == "preview");
        Assert.Equal((0, 1), (result.Summary?.Deployed, result.Summary?.Failed));
        Assert.Equal("shop/dev/thing/group", Assert.Single(result.Runs).Run.Nodes[^1].NodeId);
        Assert.Equal(Assert.Single(RecordNames("deploy")), result.Record);
    }

    [Fact]
    public async Task A_failed_iq_leaves_the_deployment_is_recorded_and_fails_the_run()
    {
        var drifted = new FakeLiveRevisions(_ => [new LiveRevision("app--1", 100, ["registry.example/other:latest"])]);
        var connections = Connections() with { LiveRevisions = () => drifted };

        var result = await DeployRelease.RunAsync(
            new DeployReleaseInput(await SetAsync(), Document("envs/dev.yaml", WorkloadUseCaseTests.Environment), Catalog, false), connections, null, CancellationToken.None);

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.False(result.Iq?.Passed);
        Assert.Null(result.Problem);
        Assert.Equal(1, result.Summary?.Deployed);
        Assert.Equal(Assert.Single(RecordNames("deploy")), result.Record);
    }

    public static TheoryData<Exception> Refusals => new()
    {
        new IOException("No space left on device."),
        new UnauthorizedAccessException("No space left on device."),
    };

    // Security boundary: a record that cannot be written fails the run, and what was deployed stays deployed.
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_deploy_record_that_cannot_be_written_is_record_not_written_and_the_deployment_stays(Exception refusal)
    {
        _store = new RefusingRecords(_records, refusal);

        var result = await DeployAsync();

        Assert.Equal<Problem?>(new RecordNotWritten("No space left on device."), result.Problem);
        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Null(result.Record);
        Assert.True(result.Iq?.Passed);
        Assert.Equal(1, result.Summary?.Deployed);
        Assert.Contains(_backend.Calls, c => c.Op == "deploy");
        Assert.Empty(RecordNames("deploy"));
    }

    // Security boundary: a qualification checks the deployment and never changes it.
    [Fact]
    public async Task A_qualification_never_deploys_and_records_its_iq()
    {
        await DeployAsync();
        _backend.Calls.Clear();

        var result = await QualifyAsync();

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.DoesNotContain(_backend.Calls, c => c.Op == "deploy");
        Assert.Contains(_backend.Calls, c => c.Op == "preview");
        Assert.True(result.Iq?.Passed);
        Assert.Equal(("1.0.0", "dev"), (result.Set?.Label, result.Environment?.Name));
        Assert.Equal(Assert.Single(RecordNames("qualify")), result.Record);
    }

    [Fact]
    public async Task A_failed_qualification_is_recorded_and_fails()
    {
        var drifted = new FakeLiveRevisions(_ => [new LiveRevision("app--1", 100, ["registry.example/other:latest"])]);

        var result = await QualifyRelease.RunAsync(
            new QualifyReleaseInput(await SetAsync(), Document("envs/dev.yaml", WorkloadUseCaseTests.Environment), Catalog), Connections() with { LiveRevisions = () => drifted }, CancellationToken.None);

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.False(result.Iq?.Passed);
        Assert.Equal(Assert.Single(RecordNames("qualify")), result.Record);
    }

    [Fact]
    public async Task A_qualify_record_that_cannot_be_written_is_record_not_written()
    {
        await DeployAsync();
        _store = new RefusingRecords(_records, new IOException("No space left on device."));

        var result = await QualifyAsync();

        Assert.Equal<Problem?>(new RecordNotWritten("No space left on device."), result.Problem);
        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.True(result.Iq?.Passed);
        Assert.Null(result.Record);
    }

    [Fact]
    public async Task A_start_that_refuses_returns_its_refusal_and_walks_nothing()
    {
        var gated = Document("envs/dev.yaml", WorkloadUseCaseTests.Environment + Approval);

        var deployed = await DeployRelease.RunAsync(new DeployReleaseInput(await SetAsync(), gated, Catalog, false), Connections(), null, CancellationToken.None);
        var qualified = await QualifyRelease.RunAsync(new QualifyReleaseInput(await SetAsync(), gated, Catalog), Connections(), CancellationToken.None);

        Assert.Equal((new Gated("dev"), Outcome.Failed), (deployed.Problem, deployed.Outcome));
        Assert.Equal((new Gated("dev"), Outcome.Failed), (qualified.Problem, qualified.Outcome));
        Assert.Equal((null, null, null), (deployed.Set, deployed.Summary, deployed.Record));
        Assert.Null(qualified.Iq);
        Assert.Empty(_backend.Calls);
    }
}
