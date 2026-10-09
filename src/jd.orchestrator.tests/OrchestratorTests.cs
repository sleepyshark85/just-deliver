using System.Globalization;
using jd.core.bp;
using jd.definitionvalidator;
using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.orchestrator.tests;

// Real resolver, fake backend: the graph is what the resolver makes of a tiny catalog, the backend records what it is asked.
public class OrchestratorTests
{
    private static readonly EnvironmentDescriptor Env = new("dev", "region-1", "team", new Dictionary<string, string>(), []);

    // group -> app (references group outputs); rt waits on the runtime through app.
    private const string Mapping = """
        kind: Mapping
        match: { type: thing }
        nodes:
          group:
            template: t/group
            config:
              owner: ${workload.team}
              count: 3
              enabled: true
              ratio: 0.15
              settings: { tags: { env: '${env.name}' }, zones: [a, b] }
          app:
            template: t/app
            config:
              groupId: ${group.id}
              label: pre-${group.id}-${group.name}
              key: ${group.key}
          app2:
            template: t/app2
            config:
              groupName: ${group.name}
          rt:
            template: t/rt
            config:
              principal: ${runtime.principalId}
              appId: ${app.id}
        exports:
          out: x
          key: y
        """;

    // The runtime is not deployed by the walk; it is reported, first, because its id sorts before any requirement's.
    private const string RuntimeMapping = "kind: Mapping\nmatch: { kind: runtime }\nnodes:\n  runtime:\n    template: t/runtime\n    config: { k: 1 }\n";

    private const string DefaultRequires = "  - type: thing\n";

    // The orchestrator gets the catalog the graph was resolved with.
    private static async Task<(ResolvedGraph Graph, Catalog Catalog)> ResolveAsync(
        string requires = DefaultRequires, string mapping = Mapping, string workloadName = "shop", string runtimeMapping = RuntimeMapping, string policy = "", string container = "")
    {
        var files = new[]
            {
                "kind: Catalog\nversion: \"1\"\n",
                "kind: ResourceType\nname: thing\ndescription: d\nclasses: [standard]\nexports: [out, key]\n",
                "kind: Naming\nrules:\n  thing:\n    pattern: \"{workload}-{id}\"\n    maxLength: 40\n    allowed: \"[a-z0-9-]\"\n",
                mapping,
                runtimeMapping,
            }
            .Concat(policy.Length > 0 ? [policy] : [])
            .Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var loaded = await CatalogParser.ParseAsync(files);
        Assert.Empty(loaded.Errors);
        var workload = (JObject)YamlSchemaValidator.ParseYaml($"metadata: {{ name: {workloadName}, team: crew }}\n{container}requires:\n{requires}");
        var graph = Resolver.Resolve(workload, "workload.yaml", loaded.Catalog!, Env);
        Assert.Empty(graph.Errors);
        return (graph, loaded.Catalog!);
    }

    private static async Task<RunReport> DeployRawAsync(FakeBackend backend, string requires = DefaultRequires, Action<NodeReport>? progress = null, string mapping = Mapping)
    {
        var (graph, catalog) = await ResolveAsync(requires, mapping);
        return await new Orchestrator(backend, backend, catalog, Env).DeployAsync(graph, progress, CancellationToken.None);
    }

    private static async Task<RunReport> PreviewRawAsync(FakeBackend backend)
    {
        var (graph, catalog) = await ResolveAsync();
        return await new Orchestrator(backend, backend, catalog, Env).PreviewAsync(graph, null, CancellationToken.None);
    }

    // Most tests are about the requirement's nodes; the runtime node, reported first, has its own test.
    private static RunReport WithoutRuntime(RunReport report) => new(report.Nodes.Where(n => !n.NodeId.EndsWith("/@workload/runtime", StringComparison.Ordinal)).ToList());

    private static async Task<RunReport> DeployAsync(FakeBackend backend, string requires = DefaultRequires, Action<NodeReport>? progress = null, string mapping = Mapping) =>
        WithoutRuntime(await DeployRawAsync(backend, requires, progress, mapping));

    private static async Task<RunReport> PreviewAsync(FakeBackend backend) => WithoutRuntime(await PreviewRawAsync(backend));

    private sealed class FakeBackend : IBackEndProvider, ITemplateStore
    {
        public List<(string Op, DeploymentPackage Package)> Calls { get; } = [];
        public Dictionary<string, Dictionary<string, ConfigEntry>> State { get; } = [];
        public Dictionary<string, int> Summary { get; set; } = new() { ["Create"] = 1 };
        public string? FailOn { get; set; }
        public bool OmitIdOutput { get; set; }

        public string GetContent(string templateName) => "template:" + templateName;

        public Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken)
        {
            Calls.Add(("deploy", package));
            if (package.StackName == FailOn)
            {
                throw new InvalidOperationException("provider exploded");
            }

            var outputs = new Dictionary<string, ConfigEntry> { ["name"] = new("n-" + package.StackName), ["key"] = new("k-" + package.StackName, isSecret: true) };
            if (!OmitIdOutput)
            {
                outputs["id"] = new("id-" + package.StackName);
            }

            State[package.StackName] = outputs;
            return Task.FromResult(new DeploymentResult { Outputs = outputs, Summary = Summary });
        }

        public Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken)
        {
            Calls.Add(("preview", package));
            return Task.FromResult(new DeploymentResult { Outputs = [], Summary = Summary });
        }

        public Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string stackName, string deploymentContent, CancellationToken cancellationToken)
        {
            Calls.Add(("read", new DeploymentPackage { StackName = stackName, DeploymentContent = deploymentContent, DeploymentParameters = [] }));
            return Task.FromResult(State.GetValueOrDefault(stackName));
        }
    }

    [Fact]
    public async Task Deploy_walks_infrastructure_nodes_in_order_passing_outputs_on()
    {
        var backend = new FakeBackend();

        var report = await DeployAsync(backend);

        Assert.True(report.Succeeded);
        Assert.Equal(["shop/dev/thing/group", "shop/dev/thing/app", "shop/dev/thing/app2", "shop/dev/thing/rt"], report.Nodes.Select(n => n.NodeId));
        Assert.Equal([NodeOutcome.Deployed, NodeOutcome.Deployed, NodeOutcome.Deployed, NodeOutcome.WaitingForRuntime], report.Nodes.Select(n => n.Outcome));
        Assert.Equal(["shop.dev.thing.group", "shop.dev.thing.app", "shop.dev.thing.app2"], backend.Calls.Select(c => c.Package.StackName));
        Assert.Equal("template:t/app", backend.Calls[1].Package.DeploymentContent);
        var app = backend.Calls[1].Package.DeploymentParameters;
        Assert.Equal("id-shop.dev.thing.group", app["groupId"].Value);
        Assert.Equal("pre-id-shop.dev.thing.group-n-shop.dev.thing.group", app["label"].Value);
        Assert.Equal(new Dictionary<string, int> { ["Create"] = 1 }, report.Nodes[0].Summary);
        Assert.All(report.Nodes, n => Assert.True(n.Elapsed >= TimeSpan.Zero));
    }

    [Fact]
    public async Task A_release_deploys_the_workloads_in_the_order_given()
    {
        var backend = new FakeBackend();
        var (alpha, catalog) = await ResolveAsync(workloadName: "alpha");
        var (beta, _) = await ResolveAsync(workloadName: "beta");
        var started = new List<string>();

        var runs = await new Orchestrator(backend, backend, catalog, Env).DeployReleaseAsync([beta, alpha], preview: false, started.Add, null, CancellationToken.None);

        Assert.Equal(["beta", "alpha"], started);
        Assert.Equal(["beta", "alpha"], runs.Select(r => r.Workload));
        Assert.All(runs, r => Assert.True(r.Run.Succeeded));
        Assert.Equal(["beta.dev.thing.group", "beta.dev.thing.app", "beta.dev.thing.app2", "alpha.dev.thing.group", "alpha.dev.thing.app", "alpha.dev.thing.app2"], backend.Calls.Select(c => c.Package.StackName));
    }

    [Fact]
    public async Task A_release_stops_after_the_first_failing_workload_and_never_touches_the_next()
    {
        var backend = new FakeBackend { FailOn = "alpha.dev.thing.app" };
        var (alpha, catalog) = await ResolveAsync(workloadName: "alpha");
        var (beta, _) = await ResolveAsync(workloadName: "beta");
        var started = new List<string>();

        var runs = await new Orchestrator(backend, backend, catalog, Env).DeployReleaseAsync([alpha, beta], preview: false, started.Add, null, CancellationToken.None);

        var run = Assert.Single(runs);
        Assert.Equal("alpha", run.Workload);
        Assert.False(run.Run.Succeeded);
        Assert.Equal(["alpha"], started);
        Assert.DoesNotContain(backend.Calls, c => c.Package.StackName.StartsWith("beta.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_release_preview_previews_every_workload_and_deploys_none()
    {
        var backend = new FakeBackend();
        var (alpha, catalog) = await ResolveAsync(workloadName: "alpha");
        var (beta, _) = await ResolveAsync(workloadName: "beta");

        var runs = await new Orchestrator(backend, backend, catalog, Env).DeployReleaseAsync([alpha, beta], preview: true, null, null, CancellationToken.None);

        Assert.Equal(["alpha", "beta"], runs.Select(r => r.Workload));
        Assert.DoesNotContain(backend.Calls, c => c.Op == "deploy");
    }

    [Fact]
    public async Task The_report_carries_the_outputs_of_a_deploy_but_never_a_secret_one()
    {
        var report = await DeployAsync(new FakeBackend());

        Assert.Equal(["id", "name"], report.Nodes[0].Outputs.Keys.Order());
        Assert.Equal("id-shop.dev.thing.group", report.Nodes[0].Outputs["id"].Value);
        Assert.Empty(report.Nodes[3].Outputs);
    }

    [Fact]
    public async Task Config_is_converted_to_strings_numbers_and_booleans_invariantly_and_structures_to_compact_json()
    {
        var backend = new FakeBackend();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            await DeployAsync(backend);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var group = backend.Calls[0].Package.DeploymentParameters;
        Assert.Equal("crew", group["owner"].Value);
        Assert.Equal("3", group["count"].Value);
        Assert.Equal("true", group["enabled"].Value);
        Assert.Equal("0.15", group["ratio"].Value);
        Assert.Equal("""{"tags":{"env":"dev"},"zones":["a","b"]}""", group["settings"].Value);
    }

    [Fact]
    public async Task A_value_built_from_a_secret_output_is_a_secret()
    {
        var backend = new FakeBackend();

        await DeployAsync(backend);

        var app = backend.Calls[1].Package.DeploymentParameters;
        Assert.True(app["key"].IsSecret);
        Assert.False(app["groupId"].IsSecret);
    }

    [Fact]
    public async Task Deploy_reports_unchanged_when_the_provider_reports_no_changes()
    {
        var backend = new FakeBackend { Summary = new() { ["Same"] = 2 } };

        var report = await DeployAsync(backend);

        Assert.Equal([NodeOutcome.Unchanged, NodeOutcome.Unchanged, NodeOutcome.Unchanged, NodeOutcome.WaitingForRuntime], report.Nodes.Select(n => n.Outcome));
    }

    [Fact]
    public async Task A_reference_without_a_value_stops_the_deploy_naming_node_field_and_reference()
    {
        var backend = new FakeBackend { OmitIdOutput = true };

        var report = await DeployAsync(backend);

        Assert.False(report.Succeeded);
        var failed = report.Nodes[^1];
        Assert.Equal(("shop/dev/thing/app", NodeOutcome.Failed), (failed.NodeId, failed.Outcome));
        Assert.Contains("field 'groupId' references group.id", failed.Message);
        Assert.Contains("field 'label' references group.id", failed.Message);
        Assert.Equal(["shop.dev.thing.group"], backend.Calls.Select(c => c.Package.StackName));
    }

    [Fact]
    public async Task Deploy_stops_at_the_first_failure()
    {
        var backend = new FakeBackend { FailOn = "shop.dev.thing.group" };

        var report = await DeployAsync(backend);

        var only = Assert.Single(report.Nodes);
        Assert.Equal(NodeOutcome.Failed, only.Outcome);
        Assert.Equal("provider exploded", only.Message);
        Assert.False(report.Succeeded);
        Assert.Single(backend.Calls);
    }

    [Fact]
    public async Task Nodes_with_the_same_name_in_different_scopes_get_their_own_scope_outputs()
    {
        var backend = new FakeBackend();

        await DeployAsync(backend, "  - type: thing\n    id: one\n  - type: thing\n    id: two\n");

        var apps = backend.Calls.Where(c => c.Package.StackName.EndsWith(".app", StringComparison.Ordinal)).ToDictionary(c => c.Package.StackName, c => c.Package.DeploymentParameters["groupId"].Value);
        Assert.Equal("id-shop.dev.one.group", apps["shop.dev.one.app"]);
        Assert.Equal("id-shop.dev.two.group", apps["shop.dev.two.app"]);
    }

    [Fact]
    public async Task Progress_is_reported_per_node_as_it_completes()
    {
        var seen = new List<string>();

        await DeployRawAsync(new FakeBackend(), progress: r => seen.Add(r.NodeId));

        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task Preview_fills_references_from_deployed_state_and_previews_without_deploying()
    {
        var backend = new FakeBackend();
        backend.State["shop.dev.thing.group"] = new() { ["id"] = new("id-from-state"), ["name"] = new("name-from-state"), ["key"] = new("k", isSecret: true) };

        var report = await PreviewAsync(backend);

        Assert.Equal([NodeOutcome.Previewed, NodeOutcome.Previewed, NodeOutcome.Previewed, NodeOutcome.WaitingForRuntime], report.Nodes.Select(n => n.Outcome));
        Assert.DoesNotContain(backend.Calls, c => c.Op == "deploy");
        var app = backend.Calls.Single(c => c.Op == "preview" && c.Package.StackName == "shop.dev.thing.app").Package.DeploymentParameters;
        Assert.Equal("id-from-state", app["groupId"].Value);
        Assert.Equal("pre-id-from-state-name-from-state", app["label"].Value);
    }

    [Fact]
    public async Task Preview_marks_a_node_pending_upstream_when_its_references_cannot_be_filled()
    {
        var backend = new FakeBackend();

        var report = await PreviewAsync(backend);

        Assert.Equal([NodeOutcome.Previewed, NodeOutcome.PendingUpstream, NodeOutcome.PendingUpstream, NodeOutcome.WaitingForRuntime], report.Nodes.Select(n => n.Outcome));
        Assert.True(report.Succeeded);
        Assert.Contains("field 'groupId' references group.id", report.Nodes[1].Message);
        Assert.Equal(["shop.dev.thing.group"], backend.Calls.Where(c => c.Op == "preview").Select(c => c.Package.StackName));
    }

    [Fact]
    public async Task The_runtime_node_is_reported_first_as_not_deployed_by_the_walk_in_deploy_and_preview()
    {
        var backend = new FakeBackend();

        var deployed = await DeployRawAsync(backend);
        var previewed = await PreviewRawAsync(backend);

        Assert.Equal("shop/dev/@workload/runtime", deployed.Nodes[0].NodeId);
        Assert.Equal(NodeOutcome.WaitingForRuntime, deployed.Nodes[0].Outcome);
        Assert.Equal(NodeOutcome.WaitingForRuntime, previewed.Nodes[0].Outcome);
        Assert.Contains("the runtime is deployed in a later step", deployed.Nodes[0].Message);
        Assert.DoesNotContain(backend.Calls, c => c.Package.StackName.EndsWith("._workload.runtime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_node_that_depends_on_the_runtime_is_neither_deployed_nor_previewed()
    {
        var backend = new FakeBackend();

        var deployed = await DeployAsync(backend);
        var previewed = await PreviewAsync(backend);

        Assert.DoesNotContain(backend.Calls, c => c.Package.StackName.EndsWith(".rt", StringComparison.Ordinal));
        Assert.Equal(NodeOutcome.WaitingForRuntime, deployed.Nodes[^1].Outcome);
        Assert.Equal(NodeOutcome.WaitingForRuntime, previewed.Nodes[^1].Outcome);
    }

    [Fact]
    public async Task Preview_reads_each_upstream_node_from_state_once()
    {
        var backend = new FakeBackend();

        await PreviewAsync(backend); // app and app2 both depend on group, which is not deployed

        Assert.Single(backend.Calls, c => c.Op == "read");
    }

    [Fact]
    public async Task A_graph_resolved_with_another_catalog_or_environment_is_refused()
    {
        var (graph, catalog) = await ResolveAsync();
        var backend = new FakeBackend();

        await Assert.ThrowsAsync<ArgumentException>(() => new Orchestrator(backend, backend, catalog with { Version = "2" }, Env).DeployAsync(graph, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => new Orchestrator(backend, backend, catalog, Env with { Name = "prod" }).PreviewAsync(graph, null, CancellationToken.None));
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_null_config_value_fails_naming_node_and_field_and_is_never_sent_as_text()
    {
        var backend = new FakeBackend();
        var mapping = "kind: Mapping\nmatch: { type: thing }\nnodes:\n  n:\n    template: t/n\n    config: { a: { b: ~ } }\nexports:\n  out: x\n  key: y\n";

        var report = await DeployAsync(backend, mapping: mapping);

        var failed = Assert.Single(report.Nodes);
        Assert.Equal(NodeOutcome.Failed, failed.Outcome);
        Assert.Contains("node 'shop/dev/thing/n': field 'a.b' is null", failed.Message);
        Assert.Empty(backend.Calls);
    }

    // Only the runtime node reads the workload's variables and the requirements' exports, and the walk does not deploy it until S16. These
    // tests make it a node of the infrastructure phase, as the release flow will see it, and check the second pass on it. 'group' is a
    // node name in two scopes (the runtime mapping declares one too): the runtime reads its own scope's by name and the requirement's only through an export.
    private const string ExportingMapping = """
        kind: Mapping
        match: { type: thing }
        nodes:
          group:
            template: t/group
            config: { owner: '${workload.team}' }
        exports:
          out: pre-${group.id}
          key: ${group.key}
        """;

    private const string RuntimeReadingMapping = """
        kind: Mapping
        match: { kind: runtime }
        nodes:
          group:
            template: t/group
            config: { owner: x }
          runtime:
            template: t/runtime
            config:
              own: ${group.name}
              label: ${name('thing')}-${resource.thing.out}
              image: ${workload.image}:${workload.port}-${resource.thing.out}
              settled: ${name('thing')}
              variables:
                fn::entries: workload.variables
        """;

    private static async Task<(RunReport Report, ResolvedGraph Graph)> DeployRuntimeAsync(FakeBackend backend, string variables)
    {
        var (graph, catalog) = await ResolveAsync(
            DefaultRequires, ExportingMapping, runtimeMapping: RuntimeReadingMapping,
            container: $"container:\n  image: reg/app:1\n  ports:\n    - port: 8080\n  variables:\n{variables}");
        var asDeployed = graph with { Nodes = graph.Nodes.Select(n => n.Name == "runtime" ? n with { Phase = Phase.Infrastructure } : n).ToList() };
        return (await new Orchestrator(backend, backend, catalog, Env).DeployAsync(asDeployed, null, CancellationToken.None), graph);
    }

    private static Dictionary<string, ConfigEntry> Deployed(FakeBackend backend, string stack) =>
        backend.Calls.Single(c => c.Package.StackName == stack).Package.DeploymentParameters;

    [Fact]
    public async Task A_pending_workload_variable_is_resolved_in_the_second_pass_and_the_variables_are_a_list_sorted_by_name()
    {
        var backend = new FakeBackend();

        var (report, _) = await DeployRuntimeAsync(backend, "    ZED: '${resource.thing.out}'\n    ALPHA: lit\n    MID: 'x-${resource.thing.out}'\n");

        Assert.True(report.Succeeded);
        var runtime = Deployed(backend, "shop.dev._workload.runtime");
        const string exported = "pre-id-shop.dev.thing.group";
        Assert.Equal($$"""[{"name":"ALPHA","value":"lit"},{"name":"MID","value":"x-{{exported}}"},{"name":"ZED","value":"{{exported}}"}]""", runtime["variables"].Value);
        Assert.False(runtime["variables"].IsSecret);
        // The export is deployed first, and the runtime comes after both nodes it reads.
        var order = backend.Calls.Select(c => c.Package.StackName).ToList();
        Assert.True(order.IndexOf("shop.dev.thing.group") < order.IndexOf("shop.dev._workload.runtime"));
        Assert.True(order.IndexOf("shop.dev._workload.group") < order.IndexOf("shop.dev._workload.runtime"));
    }

    [Fact]
    public async Task A_node_that_reads_no_export_does_not_fail_on_an_export_it_cannot_evaluate()
    {
        var (graph, catalog) = await ResolveAsync();
        var broken = new Pending("${env.nope}-${group.id}", new HashSet<Reference> { new(ReferenceKind.Node, "group", "id") });
        var withBrokenExport = graph with { Exports = new Dictionary<string, IReadOnlyDictionary<string, EvalResult>> { ["thing"] = new Dictionary<string, EvalResult> { ["out"] = broken } } };
        var backend = new FakeBackend();

        var report = await new Orchestrator(backend, backend, catalog, Env).DeployAsync(withBrokenExport, null, CancellationToken.None);

        Assert.True(report.Succeeded);
    }

    [Fact]
    public async Task Each_scope_gives_the_runtime_its_own_value_for_a_node_name_they_share()
    {
        var backend = new FakeBackend();

        await DeployRuntimeAsync(backend, "    A: '${resource.thing.out}'\n");

        var runtime = Deployed(backend, "shop.dev._workload.runtime");
        // ${group.name}: the workload's group. The variable reads thing's group, only through its export.
        Assert.Equal("n-shop.dev._workload.group", runtime["own"].Value);
        Assert.Contains("pre-id-shop.dev.thing.group", runtime["variables"].Value);
        Assert.DoesNotContain("_workload.group", runtime["variables"].Value);
    }

    [Fact]
    public async Task A_variable_that_reads_a_secret_output_makes_the_variables_entry_secret()
    {
        var backend = new FakeBackend();

        var (report, _) = await DeployRuntimeAsync(backend, "    PLAIN: lit\n    CONNECTION: '${resource.thing.key}'\n");

        Assert.True(report.Succeeded);
        var runtime = Deployed(backend, "shop.dev._workload.runtime");
        Assert.True(runtime["variables"].IsSecret);
        Assert.Contains("k-shop.dev.thing.group", runtime["variables"].Value);
        Assert.False(runtime["own"].IsSecret);
    }

    [Fact]
    public async Task The_second_pass_evaluates_name_image_and_port_of_the_runtime_node_as_the_first_did()
    {
        var backend = new FakeBackend();

        var (_, graph) = await DeployRuntimeAsync(backend, "    A: lit\n");

        // 'settled' has no reference, so the resolver fixed it with the node's own name; 'label' waits for an output, so the second
        // pass evaluates name() again and must use the same name. The image and port are the graph's.
        var runtimeNode = graph.Nodes.Single(n => n.Name == "runtime");
        var settled = Assert.IsType<Resolved>(Assert.IsType<ConfigText>(runtimeNode.Config["settled"]).Result).Value;
        Assert.IsType<Pending>(Assert.IsType<ConfigText>(runtimeNode.Config["label"]).Result);
        var runtime = Deployed(backend, "shop.dev._workload.runtime");
        Assert.Equal(("shop-runtime", "shop-runtime-pre-id-shop.dev.thing.group"), (settled, runtime["label"].Value));
        Assert.Equal("reg/app:1:8080-pre-id-shop.dev.thing.group", runtime["image"].Value);
    }
}
