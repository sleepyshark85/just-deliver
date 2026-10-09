using System.Globalization;
using jd.core.bp;
using jd.definitionvalidator;
using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
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
          rt:
            template: t/rt
            config:
              principal: ${runtime.principalId}
              appId: ${app.id}
        exports:
          out: x
        """;

    private static async Task<ResolvedGraph> ResolveAsync(string requires = "  - type: thing\n")
    {
        var files = new[] { "kind: Catalog\nversion: \"1\"\n", "kind: ResourceType\nname: thing\ndescription: d\nclasses: [standard]\nexports: [out]\n", Mapping }
            .Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var loaded = await CatalogParser.ParseAsync(files);
        Assert.Empty(loaded.Errors);
        var workload = (JObject)YamlSchemaValidator.ParseYaml($"metadata: {{ name: shop, team: crew }}\nrequires:\n{requires}");
        var graph = Resolver.Resolve(workload, "workload.yaml", loaded.Catalog!, Env);
        Assert.Empty(graph.Errors);
        return graph;
    }

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

    private static Orchestrator Create(FakeBackend backend) => new(backend, backend, new Catalog("1", [], [], [], new Dictionary<string, NamingRule>(), new Dictionary<string, string>()), Env);

    [Fact]
    public async Task Deploy_walks_infrastructure_nodes_in_order_passing_outputs_on()
    {
        var backend = new FakeBackend();

        var report = await Create(backend).DeployAsync(await ResolveAsync(), null, CancellationToken.None);

        Assert.True(report.Succeeded);
        Assert.Equal(["shop/dev/thing/group", "shop/dev/thing/app", "shop/dev/thing/rt"], report.Nodes.Select(n => n.NodeId));
        Assert.Equal([NodeOutcome.Deployed, NodeOutcome.Deployed, NodeOutcome.WaitingForRuntime], report.Nodes.Select(n => n.Outcome));
        Assert.Equal(["shop.dev.thing.group", "shop.dev.thing.app"], backend.Calls.Select(c => c.Package.StackName));
        Assert.Equal("template:t/app", backend.Calls[1].Package.DeploymentContent);
        var app = backend.Calls[1].Package.DeploymentParameters;
        Assert.Equal("id-shop.dev.thing.group", app["groupId"].Value);
        Assert.Equal("pre-id-shop.dev.thing.group-n-shop.dev.thing.group", app["label"].Value);
        Assert.Equal(new Dictionary<string, int> { ["Create"] = 1 }, report.Nodes[0].Summary);
        Assert.All(report.Nodes, n => Assert.True(n.Elapsed >= TimeSpan.Zero));
    }

    [Fact]
    public async Task Config_is_converted_to_strings_numbers_and_booleans_invariantly_and_structures_to_compact_json()
    {
        var backend = new FakeBackend();
        var graph = await ResolveAsync();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            await Create(backend).DeployAsync(graph, null, CancellationToken.None);
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

        await Create(backend).DeployAsync(await ResolveAsync(), null, CancellationToken.None);

        var app = backend.Calls[1].Package.DeploymentParameters;
        Assert.True(app["key"].IsSecret);
        Assert.False(app["groupId"].IsSecret);
    }

    [Fact]
    public async Task Deploy_reports_unchanged_when_the_provider_reports_no_changes()
    {
        var backend = new FakeBackend { Summary = new() { ["Same"] = 2 } };

        var report = await Create(backend).DeployAsync(await ResolveAsync(), null, CancellationToken.None);

        Assert.Equal([NodeOutcome.Unchanged, NodeOutcome.Unchanged, NodeOutcome.WaitingForRuntime], report.Nodes.Select(n => n.Outcome));
    }

    [Fact]
    public async Task A_reference_without_a_value_stops_the_deploy_naming_node_field_and_reference()
    {
        var backend = new FakeBackend { OmitIdOutput = true };

        var report = await Create(backend).DeployAsync(await ResolveAsync(), null, CancellationToken.None);

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

        var report = await Create(backend).DeployAsync(await ResolveAsync(), null, CancellationToken.None);

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

        await Create(backend).DeployAsync(await ResolveAsync("  - type: thing\n    id: one\n  - type: thing\n    id: two\n"), null, CancellationToken.None);

        var apps = backend.Calls.Where(c => c.Package.StackName.EndsWith(".app", StringComparison.Ordinal)).ToDictionary(c => c.Package.StackName, c => c.Package.DeploymentParameters["groupId"].Value);
        Assert.Equal("id-shop.dev.one.group", apps["shop.dev.one.app"]);
        Assert.Equal("id-shop.dev.two.group", apps["shop.dev.two.app"]);
    }

    [Fact]
    public async Task Progress_is_reported_per_node_as_it_completes()
    {
        var seen = new List<string>();

        await Create(new FakeBackend()).DeployAsync(await ResolveAsync(), r => seen.Add(r.NodeId), CancellationToken.None);

        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public async Task Preview_fills_references_from_deployed_state_and_previews_without_deploying()
    {
        var backend = new FakeBackend();
        backend.State["shop.dev.thing.group"] = new() { ["id"] = new("id-from-state"), ["name"] = new("name-from-state"), ["key"] = new("k", isSecret: true) };

        var report = await Create(backend).PreviewAsync(await ResolveAsync(), null, CancellationToken.None);

        Assert.Equal([NodeOutcome.Previewed, NodeOutcome.Previewed, NodeOutcome.WaitingForRuntime], report.Nodes.Select(n => n.Outcome));
        Assert.DoesNotContain(backend.Calls, c => c.Op == "deploy");
        var app = backend.Calls.Single(c => c.Op == "preview" && c.Package.StackName == "shop.dev.thing.app").Package.DeploymentParameters;
        Assert.Equal("id-from-state", app["groupId"].Value);
        Assert.Equal("pre-id-from-state-name-from-state", app["label"].Value);
    }

    [Fact]
    public async Task Preview_marks_a_node_pending_upstream_when_its_references_cannot_be_filled()
    {
        var backend = new FakeBackend();

        var report = await Create(backend).PreviewAsync(await ResolveAsync(), null, CancellationToken.None);

        Assert.Equal([NodeOutcome.Previewed, NodeOutcome.PendingUpstream, NodeOutcome.WaitingForRuntime], report.Nodes.Select(n => n.Outcome));
        Assert.True(report.Succeeded);
        Assert.Contains("field 'groupId' references group.id", report.Nodes[1].Message);
        Assert.Equal(["shop.dev.thing.group"], backend.Calls.Where(c => c.Op == "preview").Select(c => c.Package.StackName));
    }

    [Fact]
    public async Task A_node_that_depends_on_the_runtime_is_neither_deployed_nor_previewed()
    {
        var backend = new FakeBackend();

        var deployed = await Create(backend).DeployAsync(await ResolveAsync(), null, CancellationToken.None);
        var previewed = await Create(backend).PreviewAsync(await ResolveAsync(), null, CancellationToken.None);

        Assert.DoesNotContain(backend.Calls, c => c.Package.StackName.EndsWith(".rt", StringComparison.Ordinal));
        Assert.Equal(NodeOutcome.WaitingForRuntime, deployed.Nodes[^1].Outcome);
        Assert.Equal(NodeOutcome.WaitingForRuntime, previewed.Nodes[^1].Outcome);
    }
}
