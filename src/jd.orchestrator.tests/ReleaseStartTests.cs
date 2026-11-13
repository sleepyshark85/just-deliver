using System.Security.Cryptography;
using System.Text;
using jd.core.identity;
using jd.core.records;
using jd.core.revisions;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.tests;
using Xunit;
using static jd.orchestrator.tests.WorkloadUseCaseTests;

namespace jd.orchestrator.tests;

// The start release deploy and release qualify share, over documents and fakes only: the order of its checks and of the connections it builds, what
// each refusal returns, and that a refused start keeps no set and makes no backend call (docs/architecture/application.md sections 5 and 8).
public sealed class ReleaseStartTests
{
    private const string SetName = "r.set.yaml";
    private const string EnvironmentName = "envs/dev.yaml";
    private const string Manifest = "kind: ReleaseManifest\nlabel: 1.0.0\nworkloads:\n  - definition: shop.yaml\n";
    private const string KeptSet = "dev/1.0.0/set.yaml";

    // The dev environment of WorkloadUseCaseTests, which System.Environment would hide.
    private const string Dev = WorkloadUseCaseTests.Environment;

    private static readonly DeployerIdentity Deployer = new("tenant-1", "client-1", "object-1");

    private readonly FakeBackend _backend = new();
    private readonly MemoryRecordStore _records = new();
    private readonly FakeIdentity _identity = new();

    // Each factory the start invoked, in order.
    private readonly List<string> _built = [];

    private sealed class FakeIdentity : IDeployerIdentity
    {
        public Exception? Failure { get; set; }

        public Task<DeployerIdentity> GetAsync(CancellationToken cancellationToken) => Failure is null ? Task.FromResult(Deployer) : throw Failure;
    }

    private T Build<T>(string factory, Func<T> build)
    {
        _built.Add(factory);
        return build();
    }

    private Connections Connections() => new(
        () => Build("records", () => (IRecordStore)_records),
        () => Build("identity", () => (IDeployerIdentity)_identity),
        () => Build("live revisions", () => (ILiveRevisions)new FakeLiveRevisions(_ => [])),
        () => Build("backend", () => (jd.core.bp.IBackEndProvider)_backend),
        () => Build("probe", () => (IRevisionProbe)new FakeProbe(_backend.Log)),
        () => Build("time", () => TimeProvider.System),
        part => Build("templates", () => new RecordingConnections(_backend).With().Templates(part)));

    // The bytes of a set of the one workload of WorkloadUseCaseTests, as release create writes them; the workload's requirement can be renamed.
    internal static async Task<byte[]> SetAsync(string requirement = "thing")
    {
        var created = await CreateRelease.RunAsync(
            new CreateReleaseInput(
                Document("r.manifest.yaml", Manifest),
                (reference, _) => Task.FromResult((reference, (ReadOnlyMemory<byte>?)Document(reference, Workload.Replace("- type: thing", $"- type: {requirement}", StringComparison.Ordinal)).Content))),
            new Connections(
                () => throw new NotSupportedException(), () => throw new NotSupportedException(), () => throw new NotSupportedException(),
                () => throw new NotSupportedException(), () => throw new NotSupportedException(), () => TimeProvider.System, _ => throw new NotSupportedException()),
            CancellationToken.None);
        Assert.NotNull(created.Document);
        return created.Document.Value.ToArray();
    }

    private static DeployReleaseInput Given(byte[] set, string environment = Dev, bool preview = false) =>
        new(new SourceDocument(SetName, set), Document(EnvironmentName, environment), Catalog, preview);

    private Task<ReleaseStartResult> StartAsync(DeployReleaseInput input) => ReleaseStart.StartAsync(input, null, Connections(), CancellationToken.None);

    // The walk of a promotion to the gated environment: the set kept on dev, as Promote names it, and the hash the promotion judged.
    private Task<ReleaseStartResult> PromoteAsync(byte[] kept, string judged) =>
        ReleaseStart.StartAsync(
            new DeployReleaseInput(new SourceDocument(KeptSet, kept), Document(EnvironmentName, Dev + Approval), Catalog, false), new Promotion(Deployer, judged), Connections(), CancellationToken.None);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task A_set_that_passes_every_check_starts_with_the_set_kept_and_everything_built_and_nothing_deployed()
    {
        var set = await SetAsync();

        var result = await StartAsync(Given(set));

        Assert.Empty(result.Errors);
        Assert.Null(result.Problem);
        var run = Assert.IsType<StartedRelease>(result.Run);
        Assert.Equal(("1.0.0", "dev"), (run.Set.Label, run.Environment.Name));
        Assert.Equal(["shop"], run.Graphs.Select(g => g.Workload));
        Assert.Equal((Deployer, Hash(set)), (run.Recording?.Identity, run.Recording?.SetSha256));
        Assert.Equal(set, _records.Files[KeptSet]);
        Assert.Equal(["records", "time", "templates", "identity", "backend", "probe", "live revisions"], _built);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task A_preview_records_nothing_and_has_no_recording_or_qualifier()
    {
        var result = await StartAsync(Given(await SetAsync(), preview: true));

        Assert.NotNull(result.Run);
        Assert.Null(result.Run.Recording);
        Assert.Equal(["time", "templates", "backend", "probe"], _built);
        Assert.Empty(_records.Files);
    }

    // Security boundary: the gate refuses before the identity, the records or the backend is touched.
    [Fact]
    public async Task The_gate_refuses_a_gated_environment_before_the_identity_the_records_or_the_backend()
    {
        var result = await StartAsync(Given(await SetAsync(), Dev + Approval));

        Assert.Equal<Problem?>(new Gated("dev"), result.Problem);
        Assert.Null(result.Run);
        Assert.Empty(result.Errors);
        Assert.Equal(["records", "time"], _built);
        Assert.Empty(_records.Files);
    }

    [Fact]
    public async Task A_preview_of_a_gated_environment_is_not_refused()
    {
        var result = await StartAsync(Given(await SetAsync(), Dev + Approval, preview: true));

        Assert.Null(result.Problem);
        Assert.NotNull(result.Run);
    }

    // Security boundary: the identity is checked before set.yaml is kept, and that before the backend factory is called.
    [Fact]
    public async Task A_refused_identity_keeps_no_set_and_builds_no_backend()
    {
        _identity.Failure = new InvalidOperationException("the credential was refused.");

        var result = await StartAsync(Given(await SetAsync()));

        Assert.Equal<Problem?>(new IdentityRefused("the credential was refused."), result.Problem);
        Assert.Null(result.Run);
        Assert.Empty(_records.Files);
        Assert.Equal(["records", "time", "templates", "identity"], _built);
    }

    [Fact]
    public async Task The_set_is_kept_before_the_backend_is_built()
    {
        var set = await SetAsync();
        bool? keptFirst = null;
        var connections = Connections() with
        {
            Backend = () =>
            {
                keptFirst = _records.Files.ContainsKey(KeptSet);
                return _backend;
            },
        };

        await ReleaseStart.StartAsync(Given(set), null, connections, CancellationToken.None);

        Assert.True(keptFirst);
    }

    // Security boundary: a recording conflict deploys nothing, and the kept set stays as it was.
    [Fact]
    public async Task Another_set_under_a_kept_label_is_refused_before_the_backend_is_built()
    {
        var kept = await SetAsync();
        _records.Files[KeptSet] = kept;
        var other = await SetAsync();
        other = [.. other, .. "# another set\n"u8.ToArray()];

        var result = await StartAsync(Given(other));

        Assert.Equal(
            new RecordingRefused($"the kept set.yaml holds a different release set for label 1.0.0 on dev (sha256 {Hash(kept)}, this one {Hash(other)}); a label names one set, so use a new label."),
            result.Problem);
        Assert.Null(result.Run);
        Assert.Equal(kept, _records.Files[KeptSet]);
        Assert.DoesNotContain("backend", _built);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task A_label_the_records_cannot_use_is_refused_with_the_records_words()
    {
        var set = await SetAsync();
        var text = Encoding.UTF8.GetString(set).Replace("label: 1.0.0", "label: 1.0.0-rc..1", StringComparison.Ordinal);

        var result = await StartAsync(Given(Encoding.UTF8.GetBytes(text)));

        Assert.Equal(new RecordingRefused("'1.0.0-rc..1' cannot name a directory of the release records: it is empty or contains '..', '/' or '\\'."), result.Problem);
        Assert.Empty(_records.Files);
        Assert.DoesNotContain("backend", _built);
    }

    // Risk c (application.md section 8): every connection is built before the first backend call, so one that cannot be built stops the run with
    // nothing deployed.
    [Theory]
    [InlineData("backend")]
    [InlineData("probe")]
    [InlineData("live revisions")]
    public async Task A_connection_that_cannot_be_built_stops_the_start_before_any_backend_call(string failing)
    {
        var connections = Connections();
        Func<T> Failing<T>(string factory, Func<T> build) => factory == failing ? () => throw new InvalidOperationException($"the {factory} cannot be built.") : build;
        connections = connections with
        {
            Backend = Failing("backend", connections.Backend),
            Probe = Failing("probe", connections.Probe),
            LiveRevisions = Failing("live revisions", connections.LiveRevisions),
        };

        var result = await ReleaseStart.StartAsync(Given(await SetAsync()), null, connections, CancellationToken.None);

        Assert.Equal<Problem?>(new ConnectionFailed($"the {failing} cannot be built."), result.Problem);
        Assert.Null(result.Run);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task A_promotion_deploys_the_kept_set_as_its_principal_without_the_gate_or_the_identity()
    {
        var set = await SetAsync();

        var result = await PromoteAsync(set, Hash(set));

        Assert.Null(result.Problem);
        Assert.Equal((Deployer, Hash(set)), (result.Run?.Recording?.Identity, result.Run?.Recording?.SetSha256));
        Assert.DoesNotContain("identity", _built);
    }

    // Security boundary: the bytes deployed are the bytes the promotion judged; a kept set that changed is refused before it is resolved (its workload
    // here does not resolve, and no resolution error is returned).
    [Fact]
    public async Task A_kept_set_whose_bytes_changed_is_refused_before_it_is_resolved_naming_the_set_and_the_judged_hash()
    {
        var judged = new string('0', 64);

        var result = await PromoteAsync(await SetAsync("no-such-type"), judged);

        Assert.Equal<Problem?>(new KeptSetChanged(KeptSet, judged), result.Problem);
        Assert.Null(result.Run);
        Assert.Empty(result.Errors);
        Assert.Equal(["records", "time"], _built);
    }

    [Fact]
    public async Task The_errors_of_the_set_the_catalog_and_the_environment_are_returned_together_the_catalogs_located()
    {
        var catalog = new SourceCatalog("cat", [.. Catalog.Documents, Document("components/oops.yaml", "kind: Component\nname: oops\ndescription: d\n")]);

        var result = await ReleaseStart.StartAsync(
            new DeployReleaseInput(Document(SetName, "kind: Nothing\n"), Document(EnvironmentName, "kind: Environment\n"), catalog, false), null, Connections(), CancellationToken.None);

        Assert.Equal(
            [
                $"{SetName}: kind: unknown kind 'Nothing'; expected one of ReleaseSet.",
                $"{Path.Combine("cat", "components/oops.yaml")}: exports: schema violation: PropertyRequired: #/exports",
                $"{Path.Combine("cat", "components/oops.yaml")}: implementations: schema violation: PropertyRequired: #/implementations",
            ],
            result.Errors.Take(3).Select(e => e.ToString()));
        Assert.All(result.Errors.Skip(3), e => Assert.Equal(EnvironmentName, e.File));
        Assert.NotEmpty(result.Errors.Skip(3));
        Assert.Equal(["records", "time"], _built);
    }

    [Fact]
    public async Task An_environment_type_the_catalog_does_not_declare_is_refused_naming_the_environment()
    {
        var result = await StartAsync(Given(await SetAsync(), Dev.Replace("type: dev", "type: gold", StringComparison.Ordinal)));

        Assert.Equal(EnvironmentName, Assert.Single(result.Errors).File);
        Assert.Null(result.Run);
        Assert.Equal(["records", "time"], _built);
    }

    [Fact]
    public async Task A_workload_that_does_not_resolve_is_refused_naming_it_in_the_set_before_the_templates_are_built()
    {
        var result = await StartAsync(Given(await SetAsync("no-such-type")));

        Assert.Contains(result.Errors, e => e.File == $"{SetName}#shop" && e.Message.Contains("'no-such-type' is not a component in the catalog", StringComparison.Ordinal));
        Assert.Null(result.Run);
        Assert.Equal(["records", "time"], _built);
    }

    [Fact]
    public async Task A_graph_that_does_not_fit_the_templates_is_refused_before_the_identity()
    {
        var misfit = new LoadError("shop/dev/thing/group", "template", "not in the library.");
        var connections = Connections() with { Templates = _ => new RecordingConnections(_backend).With(misfit).Templates(Catalog) };

        var result = await ReleaseStart.StartAsync(Given(await SetAsync()), null, connections, CancellationToken.None);

        Assert.Equal([misfit], result.Errors);
        Assert.Null(result.Run);
        Assert.Equal(["records", "time"], _built);
    }
}
