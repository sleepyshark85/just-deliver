using jd.core.bp;
using jd.core.revisions;
using jd.resolver.catalog;
using jd.resolver.graph;
using jd.resolver.release;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.orchestrator.tests;

// Installation qualification (docs/architecture/release-record.md sections 5 and 6): the preview walk finds no change, and every revision
// that holds traffic runs the pinned image. The fake backend is deployed first so that its state has what the checks read.
public partial class OrchestratorTests
{
    private const string Pinned = "registry.example/app@sha256:aaaa";
    private const string Other = "registry.example/other@sha256:bbbb";

    private static IReadOnlyList<LiveRevision> Serving(string image = Pinned) => [new("app--r1", 100, [image])];

    // The set a qualification pins: its workloads' image is the one the graphs were resolved with, which the image check compares.
    private static ReleaseSet PinnedSetOf(params string[] workloads)
    {
        var set = SetOf(workloads);
        return set with { Workloads = set.Workloads.Select(w => w with { Image = Pinned }).ToList() };
    }

    private static IReadOnlyList<LiveRevision> OneAnswer(string resourceId, string image = Pinned) => resourceId.Length == 0 ? [] : Serving(image);

    // Deploys the named workloads, then makes the fake backend preview "no change" (every stack is Same), as a clean deployment would.
    private static async Task<(FakeBackend Backend, IReadOnlyList<ResolvedGraph> Graphs, Catalog Catalog)> DeployedAsync(params string[] workloads)
    {
        var backend = new FakeBackend();
        var graphs = new List<ResolvedGraph>();
        Catalog? catalog = null;
        foreach (var name in workloads.Length == 0 ? ["shop"] : workloads)
        {
            var resolved = await ResolveAsync(workloadName: name, container: $"container:\n  image: {Pinned}\n  ports:\n    - port: 8080\n");
            graphs.Add(resolved.Graph);
            catalog = resolved.Catalog;
            Assert.True((await Create(backend, catalog).DeployAsync(resolved.Graph, null, CancellationToken.None)).Succeeded);
        }

        backend.Calls.Clear();
        backend.Summary = new() { ["Same"] = 1 };
        return (backend, graphs, catalog!);
    }

    private static Task<IqResult> QualifyAsync(FakeBackend backend, IReadOnlyList<ResolvedGraph> graphs, Catalog catalog, FakeLiveRevisions live) =>
        new Qualifier(Create(backend, catalog), backend, backend, live).QualifyAsync(graphs, CancellationToken.None);

    private static IEnumerable<string> Failures(IqResult result) => result.Checks.Where(c => !c.Passed).Select(c => c.Failure);

    [Fact]
    public async Task A_clean_deployment_running_the_pinned_image_passes_and_deploys_nothing()
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        var live = new FakeLiveRevisions(id => OneAnswer(id));

        var result = await QualifyAsync(backend, graphs, catalog, live);

        Assert.True(result.Passed, string.Join("; ", Failures(result)));
        Assert.Equal(["/apps/shop.dev._workload.runtime"], live.Calls);
        Assert.DoesNotContain(backend.Calls, c => c.Op == "deploy");
        // Every node of the graph was checked, the grant too, and the one revision.
        Assert.Equal(graphs[0].Nodes.Count, result.Checks.Count(c => c.Kind == IqCheckKind.Drift));
        Assert.Equal(["app--r1"], result.Checks.Where(c => c.Kind == IqCheckKind.Image).Select(c => c.Subject));
    }

    [Theory]
    [InlineData("Update")]
    [InlineData("Create")]
    [InlineData("Replace")]
    public async Task A_node_that_a_preview_would_change_fails_naming_the_node(string operation)
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        backend.PreviewSummaries["shop.dev.thing.app"] = new() { [operation] = 1, ["Same"] = 2 };

        var result = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(id => OneAnswer(id)));

        Assert.False(result.Passed);
        var failure = Assert.Single(result.Checks, c => !c.Passed);
        Assert.Equal((IqCheckKind.Drift, "shop/dev/thing/app"), (failure.Kind, failure.Subject));
        Assert.Contains($"node shop/dev/thing/app differs from what is deployed: {operation} 1", failure.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_drifted_grant_fails_so_the_nodes_after_the_runtime_are_previewed()
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        backend.PreviewSummaries["shop.dev.thing.rt"] = new() { ["Update"] = 1 };

        var result = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(id => OneAnswer(id)));

        Assert.Equal(["node shop/dev/thing/rt differs from what is deployed: Update 1"], Failures(result));
        Assert.Contains(backend.Calls, c => c.Op == "preview" && c.Package.StackName == "shop.dev.thing.rt");
    }

    [Fact]
    public async Task A_node_that_cannot_be_previewed_fails_and_the_nodes_that_wait_on_it_are_reported_too()
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        backend.State.Remove("shop.dev.thing.group");

        var result = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(id => OneAnswer(id)));

        Assert.False(result.Passed);
        Assert.Contains(Failures(result), f => f.StartsWith("node shop/dev/thing/app cannot be previewed (PendingUpstream)", StringComparison.Ordinal));
        Assert.Contains(Failures(result), f => f.StartsWith("node shop/dev/thing/app2 cannot be previewed (PendingUpstream)", StringComparison.Ordinal));
    }

    // A preview that throws must never qualify: the failed node fails, and every node the walk did not reach after it fails as not checked.
    [Fact]
    public async Task A_node_whose_preview_fails_fails_and_every_node_after_it_fails_as_not_checked()
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        backend.PreviewFailOn = "shop.dev.thing.group";

        var result = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(id => OneAnswer(id)));

        Assert.False(result.Passed);
        var drift = result.Checks.Where(c => c.Kind == IqCheckKind.Drift).ToList();
        Assert.Equal(graphs[0].Nodes.Count, drift.Count);
        Assert.All(drift, c => Assert.False(c.Passed, c.Subject));
        Assert.Contains(drift, c => c.Failure == "node shop/dev/thing/group cannot be previewed (Failed): provider exploded");
        Assert.All(drift.Where(c => c.Subject != "shop/dev/thing/group"), c => Assert.Contains("not checked", c.Failure, StringComparison.Ordinal));
        // All checks run: the image check still read the live revisions.
        Assert.Contains(result.Checks, c => c.Kind == IqCheckKind.Image && c.Passed);
    }

    [Fact]
    public async Task A_different_image_with_a_clean_preview_fails_saying_what_was_pinned_and_what_runs_on_which_revision()
    {
        var (backend, graphs, catalog) = await DeployedAsync();

        var result = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(id => OneAnswer(id, Other)));

        Assert.Equal([$"pinned {Pinned}, live {Other} on revision app--r1"], Failures(result));
        Assert.All(result.Checks.Where(c => c.Kind == IqCheckKind.Drift), c => Assert.True(c.Passed));
    }

    [Fact]
    public async Task The_revision_that_serves_decides_whichever_is_the_latest()
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        LiveRevision[] latestWrongServingRight = [new("app--r1", 100, [Pinned]), new("app--r2", 0, [Other])];
        LiveRevision[] latestRightServingWrong = [new("app--r1", 100, [Other]), new("app--r2", 0, [Pinned])];

        var passes = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(_ => latestWrongServingRight));
        var fails = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(_ => latestRightServingWrong));

        Assert.True(passes.Passed, string.Join("; ", Failures(passes)));
        Assert.Equal([$"pinned {Pinned}, live {Other} on revision app--r1"], Failures(fails));
    }

    [Fact]
    public async Task Every_image_of_every_revision_that_holds_traffic_must_be_the_pinned_one()
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        LiveRevision[] split = [new("app--r1", 60, [Pinned, Other]), new("app--r2", 40, [Pinned])];

        var result = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(_ => split));

        Assert.Equal([$"pinned {Pinned}, live {Other} on revision app--r1"], Failures(result));
    }

    [Fact]
    public async Task A_revision_with_traffic_that_reports_no_image_fails()
    {
        var (backend, graphs, catalog) = await DeployedAsync();

        var result = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(_ => [new("app--r1", 100, [])]));

        Assert.Equal(["revision app--r1 reports no image."], Failures(result));
    }

    [Fact]
    public async Task A_workload_with_no_revision_holding_traffic_fails()
    {
        var (backend, graphs, catalog) = await DeployedAsync();

        var result = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(_ => [new("app--r1", 0, [Pinned])]));

        Assert.Equal(["no revision of /apps/shop.dev._workload.runtime holds traffic."], Failures(result));
    }

    [Fact]
    public async Task A_reader_error_is_a_failed_check_with_the_reason_not_a_crash()
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        var live = new FakeLiveRevisions(_ => throw new InvalidOperationException("Azure refused to list the revisions: HTTP 404, ResourceNotFound."));

        var result = await QualifyAsync(backend, graphs, catalog, live);

        Assert.Equal(["the live revisions could not be checked: Azure refused to list the revisions: HTTP 404, ResourceNotFound."], Failures(result));
        Assert.All(result.Checks.Where(c => c.Kind == IqCheckKind.Drift), c => Assert.True(c.Passed));
    }

    [Fact]
    public async Task A_runtime_that_was_never_deployed_fails_without_asking_the_reader()
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        backend.State.Remove("shop.dev._workload.runtime");
        var live = new FakeLiveRevisions(id => OneAnswer(id));

        var result = await QualifyAsync(backend, graphs, catalog, live);

        Assert.Contains(Failures(result), f => f.Contains("has no 'appId' in its state", StringComparison.Ordinal));
        Assert.Empty(live.Calls);
    }

    [Fact]
    public async Task All_checks_run_so_drift_and_an_image_mismatch_are_both_listed_for_every_workload()
    {
        var (backend, graphs, catalog) = await DeployedAsync("shop", "cart");
        backend.PreviewSummaries["shop.dev.thing.group"] = new() { ["Update"] = 1 };
        backend.PreviewSummaries["cart.dev.thing.app"] = new() { ["Create"] = 1 };

        var result = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(id => OneAnswer(id, Other)));

        Assert.Equal(
            [
                ("shop", IqCheckKind.Drift, "shop/dev/thing/group"),
                ("shop", IqCheckKind.Image, "app--r1"),
                ("cart", IqCheckKind.Drift, "cart/dev/thing/app"),
                ("cart", IqCheckKind.Image, "app--r1"),
            ],
            result.Checks.Where(c => !c.Passed).Select(c => (c.Workload, c.Kind, c.Subject)));
        Assert.Contains(backend.Calls, c => c.Op == "preview" && c.Package.StackName == "cart.dev._workload.runtime");
    }

    [Fact]
    public async Task Preview_previews_the_grants_with_the_runtimes_outputs_from_state_and_never_probes_or_deploys()
    {
        var (backend, _, catalog) = await DeployedAsync();
        var (graph, _) = await ResolveAsync();
        var probe = new FakeProbe(backend.Log);

        var report = await new Orchestrator(backend, backend, catalog, Env, probe).PreviewAsync(graph, null, CancellationToken.None);

        Assert.All(report.Nodes, n => Assert.Equal(NodeOutcome.Previewed, n.Outcome));
        var rt = backend.Calls.Single(c => c.Op == "preview" && c.Package.StackName == "shop.dev.thing.rt").Package;
        Assert.Equal("p-shop.dev._workload.runtime", rt.DeploymentParameters["principal"].Value);
        Assert.Empty(probe.Calls);
        Assert.DoesNotContain(backend.Calls, c => c.Op == "deploy");
    }

    [Fact]
    public async Task A_grant_whose_preview_fails_fails_the_runtime_node_at_the_grants_step_and_deploys_and_probes_nothing()
    {
        var (backend, _, catalog) = await DeployedAsync();
        var (graph, _) = await ResolveAsync();
        backend.PreviewFailOn = "shop.dev.thing.rt";
        var probe = new FakeProbe(backend.Log);

        var report = await new Orchestrator(backend, backend, catalog, Env, probe).PreviewAsync(graph, null, CancellationToken.None);

        var runtime = Runtime(report);
        Assert.False(report.Succeeded);
        Assert.Equal(NodeOutcome.Failed, runtime.Outcome);
        Assert.StartsWith("grants: shop/dev/thing/rt: provider exploded", runtime.Message, StringComparison.Ordinal);
        Assert.Equal((Step.Grants, StepOutcome.Failed), (runtime.Steps![^1].Step, runtime.Steps[^1].Outcome));
        Assert.DoesNotContain(backend.Calls, c => c.Op == "deploy");
        Assert.Empty(probe.Calls);
    }

    // The record of a qualification of two workloads, one of them drifted and running another image; the time is fixed for the golden.
    private static async Task<(string Json, IqResult Iq)> QualifyRecordAsync()
    {
        var (backend, graphs, catalog) = await DeployedAsync("shop", "cart");
        backend.PreviewSummaries["shop.dev.thing.app"] = new() { ["Update"] = 1, ["Same"] = 3 };
        var live = new FakeLiveRevisions(id => OneAnswer(id, id.Contains("shop", StringComparison.Ordinal) ? Other : Pinned));
        var iq = (await QualifyAsync(backend, graphs, catalog, live)) with { Elapsed = TimeSpan.FromSeconds(12.5) };
        return (ReleaseRecordBuilder.BuildQualify(PinnedSetOf("shop", "cart"), SetSha, Env, graphs, Principal, RecordStart, RecordStart.AddSeconds(90), iq), iq);
    }

    [Fact]
    public async Task A_qualify_record_is_valid_against_the_schema_and_is_the_committed_golden_record()
    {
        var (record, iq) = await QualifyRecordAsync();

        Assert.False(iq.Passed);
        await AssertValidAsync(record);
        if (System.Environment.GetEnvironmentVariable("JD_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(GoldenSource())!, "qualify-record.json"), record);
        }

        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "qualify-record.json")), record);
    }

    [Fact]
    public async Task A_qualify_record_holds_the_pinned_release_and_the_checks_but_no_run_oq_or_deploy_time()
    {
        var (json, iq) = await QualifyRecordAsync();

        var record = Parse(json);

        Assert.Equal("qualify", (string?)record["activity"]);
        Assert.Equal("failed", (string?)record["iq"]!["status"]);
        Assert.Equal(12.5, (double?)record["iq"]!["elapsedSeconds"]);
        Assert.Equal(iq.Checks.Count, record["iq"]!["checks"]!.Count());
        Assert.Equal(["shop", "cart"], record["workloads"]!.Select(w => (string?)w["name"]));
        Assert.All(record["workloads"]!, w =>
        {
            Assert.NotNull(w["graph"]);
            Assert.Null(w["run"]);
            Assert.Null(w["oq"]);
        });
        Assert.Null(record["outcome"]);
        Assert.Null(record["deploySeconds"]);
        Assert.Null(record["notStarted"]);
        Assert.Contains("pinned " + Pinned + ", live " + Other + " on revision app--r1", (string?)record["iq"]!["checks"]!.Single(c => (string?)c["kind"] == "image" && (string?)c["status"] == "failed")["detail"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_qualify_record_never_holds_a_secret_output_or_a_changed_property_value()
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        var secret = backend.State["shop.dev.thing.group"]["key"].Value;
        Assert.False(string.IsNullOrEmpty(secret));
        backend.PreviewSummaries["shop.dev.thing.app"] = new() { ["Update"] = 1 };
        backend.PreviewChanges["shop.dev.thing.app"] =
            [new ResourceChange { Urn = "urn", Type = "t", Operation = "update", ChangedProperties = [new PropertyChange { Path = "p", OldValue = "old-secret-value", NewValue = "new-secret-value" }] }];
        var iq = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(id => OneAnswer(id)));
        Assert.Contains(iq.Checks, c => !c.Passed); // the drifted stack was previewed with its changes

        var json = ReleaseRecordBuilder.BuildQualify(PinnedSetOf("shop"), SetSha, Env, graphs, Principal, RecordStart, RecordStart.AddSeconds(1), iq);

        foreach (var hidden in new[] { secret!, "old-secret-value", "new-secret-value" })
        {
            Assert.DoesNotContain(hidden, json, StringComparison.Ordinal);
        }

        await AssertValidAsync(json);
    }

    [Fact]
    public async Task A_deploy_record_never_holds_a_secret_output_or_a_changed_property_value_in_its_iq()
    {
        var (backend, graphs, catalog) = await DeployedAsync();
        var secret = backend.State["shop.dev.thing.group"]["key"].Value;
        Assert.False(string.IsNullOrEmpty(secret));
        backend.PreviewSummaries["shop.dev.thing.app"] = new() { ["Update"] = 1 };
        backend.PreviewChanges["shop.dev.thing.app"] =
            [new ResourceChange { Urn = "urn", Type = "t", Operation = "update", ChangedProperties = [new PropertyChange { Path = "p", OldValue = "old-secret-value", NewValue = "new-secret-value" }] }];
        var iq = await QualifyAsync(backend, graphs, catalog, new FakeLiveRevisions(id => OneAnswer(id)));
        Assert.Contains(iq.Checks, c => !c.Passed);

        var (set, deployedGraphs, runs) = await ReleaseRunAsync(new FakeBackend(), null, "shop");
        var json = Record(set, deployedGraphs, runs, iq: iq);

        foreach (var hidden in new[] { secret!, "old-secret-value", "new-secret-value" })
        {
            Assert.DoesNotContain(hidden, json, StringComparison.Ordinal);
        }

        await AssertValidAsync(json);
        Assert.Equal("failed", (string?)Parse(json)["iq"]!["status"]);
    }

    [Fact]
    public async Task The_schema_requires_the_deploy_fields_of_a_deploy_and_the_iq_and_no_deploy_field_of_a_qualify()
    {
        var (set, graphs, runs) = await ReleaseRunAsync(new FakeBackend(), null, "shop");
        var deploy = Parse(Record(set, graphs, runs, iq: PassingIq));
        var (qualify, _) = await QualifyRecordAsync();

        foreach (var field in new[] { "outcome", "deploySeconds", "notStarted" })
        {
            var without = (JObject)deploy.DeepClone();
            without.Remove(field);
            await AssertInvalidAsync(without.ToString());
        }

        var noDeployIq = (JObject)deploy.DeepClone();
        noDeployIq.Remove("iq");
        await AssertInvalidAsync(noDeployIq.ToString());
        var notRunQualify = Parse(qualify);
        notRunQualify["iq"]!["status"] = "notRun";
        await AssertInvalidAsync(notRunQualify.ToString());
        var notRunDeploy = (JObject)deploy.DeepClone();
        notRunDeploy["iq"]!["status"] = "notRun";
        await AssertValidAsync(notRunDeploy.ToString());
        var unknownStatus = (JObject)deploy.DeepClone();
        unknownStatus["iq"]!["status"] = "skipped";
        await AssertInvalidAsync(unknownStatus.ToString());
        var noOq = (JObject)deploy.DeepClone();
        ((JObject)noOq["workloads"]![0]!).Remove("oq");
        await AssertInvalidAsync(noOq.ToString());
        var noIq = Parse(qualify);
        noIq.Remove("iq");
        await AssertInvalidAsync(noIq.ToString());
        var unknown = Parse(qualify);
        unknown["verdict"] = "ok";
        await AssertInvalidAsync(unknown.ToString());
        foreach (var field in new[] { "outcome", "deploySeconds", "notStarted" })
        {
            var withDeployField = Parse(qualify);
            withDeployField[field] = deploy[field]!.DeepClone();
            await AssertInvalidAsync(withDeployField.ToString());
        }

        var withFailure = Parse(qualify);
        withFailure["failure"] = new JObject { ["workload"] = "shop", ["node"] = "shop/dev/thing/app" };
        await AssertInvalidAsync(withFailure.ToString());

        foreach (var field in new[] { "run", "oq" })
        {
            var withWorkloadField = Parse(qualify);
            withWorkloadField["workloads"]![0]![field] = deploy["workloads"]![0]![field]!.DeepClone();
            await AssertInvalidAsync(withWorkloadField.ToString());
        }
    }
}
