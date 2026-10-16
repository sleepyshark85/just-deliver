using System.Runtime.CompilerServices;
using jd.core.bp;
using jd.core.identity;
using jd.definitionvalidator;
using jd.resolver.catalog;
using jd.resolver.graph;
using jd.resolver.release;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.orchestrator.tests;

// The release record built from real runs of the orchestrator against the fake backend (docs/architecture/release-record.md).
// Regenerate the golden record with JD_UPDATE_GOLDEN=1 and review the diff.
public partial class OrchestratorTests
{
    private const string SetSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset RecordStart = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly DeployerIdentity Principal = new("tenant-1", "client-1", "object-1");

    private static ReleaseSet SetOf(params string[] workloads) => new(
        "1.0.0",
        RecordStart.AddHours(-1),
        workloads.Select(name => new ReleaseWorkload(name, "crew", new string('a', 64), $"metadata:\n  name: {name}\n", "registry.example/app@sha256:" + new string('b', 64), [])).ToList(),
        workloads);

    // One release run over a graph per workload, in the order given; the first failure ends it.
    private static async Task<(ReleaseSet Set, IReadOnlyList<ResolvedGraph> Graphs, IReadOnlyList<WorkloadRun> Runs)> ReleaseRunAsync(FakeBackend backend, FakeProbe? probe = null, params string[] workloads)
    {
        var graphs = new List<ResolvedGraph>();
        Catalog? catalog = null;
        foreach (var name in workloads)
        {
            var resolved = await ResolveAsync(workloadName: name);
            graphs.Add(resolved.Graph);
            catalog = resolved.Catalog;
        }

        var runs = await Create(backend, catalog!, probe).DeployReleaseAsync(graphs, false, null, null, CancellationToken.None);
        return (SetOf(workloads), graphs, runs);
    }

    private static string Record(ReleaseSet set, IReadOnlyList<ResolvedGraph> graphs, IReadOnlyList<WorkloadRun> runs, DeployerIdentity? identity = null) =>
        ReleaseRecordBuilder.Build(set, SetSha, Env, graphs, runs, identity ?? Principal, RecordStart, RecordStart.AddSeconds(90), TimeSpan.FromSeconds(42.5));

    // Dates stay text: the record spells them as the schema says.
    private static JObject Parse(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static async Task AssertValidAsync(string json)
    {
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schemas", "release-record.schema.json"));
        var result = await new YamlSchemaValidator().ValidateTokenAsync(schema, Parse(json));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    private static string OqStatus(string json, int workload) => (string?)Parse(json)["workloads"]![workload]!["oq"]!["status"] ?? string.Empty;

    // The times are the machine's; a golden record needs fixed ones.
    private static WorkloadRun Timed(WorkloadRun run) => run with
    {
        Elapsed = TimeSpan.FromSeconds(4),
        Run = new RunReport(run.Run.Nodes.Select(n => n with { Elapsed = TimeSpan.FromSeconds(2), Steps = n.Steps?.Select(s => s with { Elapsed = TimeSpan.FromSeconds(0.5) }).ToList() }).ToList()),
    };

    [Fact]
    public async Task A_record_of_a_release_is_valid_against_the_schema_and_is_the_committed_golden_record()
    {
        var backend = new FakeBackend();
        var (set, graphs, runs) = await ReleaseRunAsync(backend, null, "shop", "cart");

        var record = Record(set, graphs, runs.Select(Timed).ToList());

        await AssertValidAsync(record);
        if (System.Environment.GetEnvironmentVariable("JD_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(GoldenSource(), record);
        }

        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "release-record.json")), record);
    }

    private static string GoldenSource([CallerFilePath] string testFile = "") => Path.Combine(Path.GetDirectoryName(testFile)!, "golden", "release-record.json");

    [Fact]
    public async Task A_failed_run_is_a_failed_record_naming_the_failing_workload_and_node_and_the_workloads_not_started()
    {
        var backend = new FakeBackend { FailOn = "shop.dev.thing.group" };
        var (set, graphs, runs) = await ReleaseRunAsync(backend, null, "shop", "cart", "shed");

        var json = Record(set, graphs, runs);

        await AssertValidAsync(json);
        var record = Parse(json);
        Assert.Equal("failed", (string?)record["outcome"]);
        Assert.Equal("shop", (string?)record["failure"]!["workload"]);
        Assert.Equal("shop/dev/thing/group", (string?)record["failure"]!["node"]);
        Assert.Equal(["cart", "shed"], record["notStarted"]!.Select(n => (string?)n));
        Assert.Equal(["shop", "cart", "shed"], record["workloads"]!.Select(w => (string?)w["name"]));
        Assert.Equal([true, false, false], record["workloads"]!.Select(w => w["run"] is not null));
        Assert.Equal("failed", (string?)record["workloads"]![0]!["run"]!["nodes"]![0]!["outcome"]);
        Assert.Equal("provider exploded", (string?)record["workloads"]![0]!["run"]!["nodes"]![0]!["message"]);
    }

    [Fact]
    public async Task A_succeeded_record_has_no_failure_and_the_deploy_time_apart_from_the_activity_times()
    {
        var (set, graphs, runs) = await ReleaseRunAsync(new FakeBackend(), null, "shop");

        var record = Parse(Record(set, graphs, runs));

        Assert.Equal("succeeded", (string?)record["outcome"]);
        Assert.Null(record["failure"]);
        Assert.Empty(record["notStarted"]!);
        Assert.Equal(42.5, (double?)record["deploySeconds"]);
        Assert.Equal("2026-10-17T08:00:00Z", (string?)record["startedAt"]);
        Assert.Equal("2026-10-17T08:01:30Z", (string?)record["finishedAt"]);
    }

    [Fact]
    public async Task No_secret_output_config_value_or_changed_property_value_reaches_the_record_text()
    {
        var backend = new FakeBackend();
        var (set, graphs, runs) = await ReleaseRunAsync(backend, null, "shop");
        // The fake's secret output (k-<stack>) is also the secret config value of the node that reads it; both are in the runs and the graph's inputs.
        var secretOutput = "k-shop.dev.thing.group";
        Assert.Contains(backend.Calls, c => c.Package.DeploymentParameters.Values.Any(v => v.IsSecret && v.Value == secretOutput));
        var withValues = runs.Select(r => r with
        {
            Run = new RunReport(r.Run.Nodes.Select(n => n with
            {
                Outputs = new Dictionary<string, ConfigEntry>(n.Outputs) { ["leaked"] = new("secret-output-value", isSecret: true) },
                Changes = [new ResourceChange { Urn = "urn", Type = "t", Operation = "update", ChangedProperties = [new PropertyChange { Path = "p", OldValue = "old-secret-value", NewValue = "changed-secret-value" }] }],
            }).ToList()),
        }).ToList();

        var json = Record(set, graphs, withValues);

        foreach (var secret in new[] { secretOutput, "secret-output-value", "old-secret-value", "changed-secret-value" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        await AssertValidAsync(json);
    }

    [Fact]
    public async Task The_identity_in_the_record_is_the_ports_whatever_a_definition_says()
    {
        var (_, graphs, runs) = await ReleaseRunAsync(new FakeBackend(), null, "shop");
        var set = SetOf("shop");
        var lying = set with { Workloads = [set.Workloads[0] with { Definition = "metadata:\n  name: shop\nidentity: { tenantId: evil, clientId: evil, objectId: evil }\n" }] };

        var record = Parse(Record(lying, graphs, runs, new DeployerIdentity("the-tenant", "the-client", "the-object")));

        Assert.Equal("the-tenant", (string?)record["identity"]!["tenantId"]);
        Assert.Equal("the-client", (string?)record["identity"]!["clientId"]);
        Assert.Equal("the-object", (string?)record["identity"]!["objectId"]);
        Assert.DoesNotContain("evil", record.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oq_is_passed_when_the_probe_passed_with_its_contract_detail_and_attempts()
    {
        var backend = new FakeBackend();
        var probe = new FakeProbe(backend.Log) { Answer = attempt => new ProbeResult(attempt >= 3, "HTTP " + (attempt >= 3 ? "200" : "503")) };
        var (set, graphs, runs) = await ReleaseRunAsync(backend, probe, "shop");

        var json = Record(set, graphs, runs);

        await AssertValidAsync(json);
        var oq = Parse(json)["workloads"]![0]!["oq"]!;
        Assert.Equal("passed", (string?)oq["status"]);
        Assert.Equal("/health", (string?)oq["contract"]!["path"]);
        Assert.Equal(200, (int?)oq["contract"]!["expectedStatus"]);
        Assert.Equal("HTTP 200", (string?)oq["detail"]);
        Assert.Equal(3, (int?)oq["attempts"]);
        Assert.NotNull(oq["elapsedSeconds"]);
    }

    [Fact]
    public async Task Oq_is_failed_when_the_probe_failed_and_notRun_for_the_workloads_after_it()
    {
        var backend = new FakeBackend();
        var (set, graphs, runs) = await ReleaseRunAsync(backend, Failing(backend), "shop", "cart");

        var json = Record(set, graphs, runs);

        await AssertValidAsync(json);
        var oq = Parse(json)["workloads"]![0]!["oq"]!;
        Assert.Equal("failed", (string?)oq["status"]);
        Assert.Equal("HTTP 503 after 13 attempts", (string?)oq["detail"]);
        Assert.Equal(13, (int?)oq["attempts"]);
        Assert.Equal("notRun", OqStatus(json, 1));
    }

    [Fact]
    public async Task Oq_is_unchanged_when_the_revision_was_already_live_and_the_probe_was_skipped()
    {
        var backend = new FakeBackend { DetectChanges = true };
        var (graph, catalog) = await ResolveAsync();
        var orchestrator = Create(backend, catalog);
        await orchestrator.DeployReleaseAsync([graph], false, null, null, CancellationToken.None);

        var again = await orchestrator.DeployReleaseAsync([graph], false, null, null, CancellationToken.None);

        var json = Record(SetOf("shop"), [graph], again);
        await AssertValidAsync(json);
        var oq = Parse(json)["workloads"]![0]!["oq"]!;
        Assert.Equal("unchanged", (string?)oq["status"]);
        Assert.Null(oq["detail"]);
    }

    [Theory]
    [InlineData("shop.dev.thing.rt")] // a grant: the Grants step fails before the probe
    [InlineData("shop.dev.thing.group")] // an infrastructure node: the runtime is never reached
    [InlineData("shop.dev._workload.runtime")] // the dark deploy
    public async Task Oq_is_notRun_when_the_probe_was_never_reached(string failing)
    {
        var (set, graphs, runs) = await ReleaseRunAsync(new FakeBackend { FailOn = failing }, null, "shop");

        var json = Record(set, graphs, runs);

        await AssertValidAsync(json);
        Assert.Equal("notRun", OqStatus(json, 0));
        Assert.Null(Parse(json)["workloads"]![0]!["oq"]!["attempts"]);
    }

    [Fact]
    public async Task Step_times_fit_inside_the_nodes_and_the_probe_counts_its_attempts()
    {
        var backend = new FakeBackend();
        var (_, _, runs) = await ReleaseRunAsync(backend, Failing(backend), "shop");

        var nodes = runs[0].Run.Nodes;
        var runtime = Runtime(runs[0].Run);
        Assert.All(nodes, n => Assert.True(n.Elapsed > TimeSpan.Zero));
        Assert.NotNull(runtime.Steps);
        Assert.All(runtime.Steps, s => Assert.True(s.Elapsed >= TimeSpan.Zero));
        Assert.True(runtime.Steps.Sum(s => s.Elapsed.Ticks) <= runtime.Elapsed.Ticks);
        Assert.True(runs[0].Elapsed >= nodes.Max(n => n.Elapsed));
        Assert.Equal([0, 0, 13], runtime.Steps.Select(s => s.Attempts));
    }
}
