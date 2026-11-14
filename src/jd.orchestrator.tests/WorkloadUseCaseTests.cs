using System.Text;
using jd.orchestrator.usecases;
using jd.resolver;
using Xunit;

namespace jd.orchestrator.tests;

// The preview and deploy use cases over documents only, with a fake backend, probe and templates: the order of their checks, what each refusal
// returns, and that nothing reaches the backend before every check has passed.
public sealed class WorkloadUseCaseTests
{
    private const string WorkloadName = "teams/shop/workload.yaml";
    private const string EnvironmentName = "envs/dev.yaml";

    internal const string Workload = """
        apiVersion: just-deliver/v1
        kind: Workload
        metadata:
          name: shop
          team: crew
        container:
          image: registry.example/shop@sha256:0000000000000000000000000000000000000000000000000000000000000000
        requires:
          - type: thing
        """;

    internal const string Environment = "kind: Environment\nname: dev\nregion: region-1\ntype: dev\noffers: [container-app, thing]\nvalues: {}\n";

    internal const string Approval = "approval:\n  qualifiedOn: test\n  required: 1\n  approvers: [\"00000000-0000-0000-0000-000000000000\"]\n  separateFromDeployer: true\n";

    internal static readonly SourceCatalog Catalog = new("cat",
    [
        Document("catalog.yaml", "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment, approval: false }, stage: { layer: environment, approval: true } }\n"),
        Document("components/thing.yaml",
            "kind: Component\nname: thing\ndescription: d\nexports: [out]\nimplementations:\n  default:\n    classes:\n      standard:\n"
            + "        nodes:\n          group:\n            template: t/group\n            config: { k: 1 }\n        exports:\n          out: x\n"),
        Document("runtimes/container-app.yaml",
            "kind: Runtime\nname: container-app\ndescription: d\nimplementations:\n  default:\n"
            + "    probe: { path: /health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5, scheme: https }\n"
            + "    release: { suffixInput: suffix, trafficInput: traffic, trafficOutput: traffic, suffixOutput: suffix, revisionOutput: revision, fqdnOutput: fqdn, revisionKey: revisionName, latestKey: latestRevision, weightKey: weight, resourceIdOutput: appId, suffix: { prefix: r, configDigits: 8, saltDigits: 6, maxLength: 20 }, fullWeight: 100 }\n"
            + "    nodes:\n      runtime:\n        template: t/runtime\n        config: { k: 1 }\n"),
        Document("templates/t/group/Pulumi.yaml", "name: group\n"),
    ]);

    private readonly FakeBackend _backend = new();

    internal static SourceDocument Document(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    private readonly RecordingConnections _connections;

    public WorkloadUseCaseTests() => _connections = new(_backend);

    private Task<DeployResult> DeployAsync(Connections connections, string environment = Environment, bool preview = false, List<Progress>? progress = null) =>
        Deploy.RunAsync(new DeployInput(Document(WorkloadName, Workload), Document(EnvironmentName, environment), Catalog, preview), connections, progress is null ? null : progress.Add, CancellationToken.None);

    [Fact]
    public async Task Preview_returns_the_graph_of_the_workload_in_the_environment()
    {
        var result = await Preview.RunAsync(new PreviewInput(Document(WorkloadName, Workload), Document(EnvironmentName, Environment), Catalog), CancellationToken.None);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Empty(result.Errors);
        Assert.Equal(("shop", "dev"), (result.Graph?.Workload, result.Graph?.Environment));
        Assert.Equal(["shop/dev/@workload/runtime", "shop/dev/thing/group"], result.Graph?.Nodes.Select(n => n.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Preview_refuses_an_environment_type_the_catalog_does_not_declare_naming_the_environment_document()
    {
        var result = await Preview.RunAsync(new PreviewInput(Document(WorkloadName, Workload), Document(EnvironmentName, Environment.Replace("type: dev", "type: prod")), Catalog), CancellationToken.None);

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Null(result.Graph);
        Assert.Equal(EnvironmentName, Assert.Single(result.Errors).File);
    }

    [Fact]
    public async Task Deploy_walks_the_graph_reporting_the_workload_start_before_each_finished_node()
    {
        var progress = new List<Progress>();

        var result = await DeployAsync(_connections.With(), progress: progress);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Null(result.Problem);
        Assert.Equal(["templates cat/templates t/group/Pulumi.yaml", "backend"], _connections.Built);
        Assert.Equal(["shop/dev/thing/group", "shop/dev/@workload/runtime"], result.Run?.Nodes.Select(n => n.NodeId));
        Assert.Equal([new WorkloadStarted("shop"), .. result.Run!.Nodes.Select(n => new NodeFinished(n))], progress);
    }

    [Fact]
    public async Task Deploy_refuses_a_gated_environment_with_nothing_built()
    {
        var progress = new List<Progress>();

        var result = await DeployAsync(_connections.With(), Environment.Replace("type: dev", "type: stage") + Approval, progress: progress);

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal(new Gated("dev"), result.Problem);
        Assert.Empty(result.Errors);
        Assert.Null(result.Run);
        Assert.Empty(_connections.Built);
        Assert.Empty(progress);
    }

    [Fact]
    public async Task Deploy_refuses_an_environment_type_the_catalog_does_not_declare_with_nothing_built()
    {
        var result = await DeployAsync(_connections.With(), Environment.Replace("type: dev", "type: prod"));

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Null(result.Problem);
        var error = Assert.Single(result.Errors);
        Assert.Equal((EnvironmentName, "type"), (error.File, error.Location));
        Assert.Contains("'prod'", error.Message, StringComparison.Ordinal);
        Assert.Null(result.Run);
        Assert.Empty(_connections.Built);
    }

    [Fact]
    public async Task A_preview_on_a_gated_environment_is_allowed_and_creates_nothing()
    {
        var result = await DeployAsync(_connections.With(), Environment.Replace("type: dev", "type: stage") + Approval, preview: true);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Null(result.Problem);
        Assert.All(result.Run!.Nodes, node => Assert.Equal(NodeOutcome.Previewed, node.Outcome));
        Assert.DoesNotContain(_backend.Calls, call => call.Op == "deploy");
        Assert.NotEmpty(_backend.Calls);
    }

    [Fact]
    public async Task A_template_misfit_stops_the_run_before_the_backend_is_built()
    {
        var misfit = new LoadError("shop/dev/thing/group", "template", "template 't/group' is not in the template library.");

        var result = await DeployAsync(_connections.With(misfit, misfit));

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal([misfit], result.Errors);
        Assert.Null(result.Run);
        Assert.Equal(["templates cat/templates t/group/Pulumi.yaml"], _connections.Built);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task A_failed_node_makes_the_run_failed_with_the_failed_node_last()
    {
        _backend.FailOn = "shop.dev.thing.group";

        var result = await DeployAsync(_connections.With());

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Null(result.Problem);
        Assert.Equal(NodeOutcome.Failed, result.Run?.Nodes[^1].Outcome);
    }
}
