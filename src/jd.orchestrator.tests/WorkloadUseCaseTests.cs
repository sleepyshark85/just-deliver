using System.Text;
using jd.orchestrator.usecases;
using jd.resolver;
using jd.resolver.graph;
using Xunit;

namespace jd.orchestrator.tests;

// The preview and deploy use cases over documents only, with a fake backend, probe and templates: the order of their checks, what each refusal
// returns, and that nothing reaches the backend before every check has passed.
public sealed class WorkloadUseCaseTests
{
    private const string WorkloadName = "teams/shop/workload.yaml";
    private const string EnvironmentName = "envs/dev.yaml";

    private const string Workload = """
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

    private const string Environment = "kind: Environment\nname: dev\nregion: region-1\ntype: dev\noffers: [container-app, thing]\nvalues: {}\n";

    private const string Approval = "approval:\n  qualifiedOn: test\n  required: 1\n  approvers: [\"00000000-0000-0000-0000-000000000000\"]\n  separateFromDeployer: true\n";

    private static readonly SourceCatalog Catalog = new("cat",
    [
        Document("catalog.yaml", "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment, approval: false }, stage: { layer: environment, approval: true } }\n"),
        Document("components/thing.yaml",
            "kind: Component\nname: thing\ndescription: d\nexports: [out]\nimplementations:\n  default:\n    classes:\n      standard:\n"
            + "        nodes:\n          group:\n            template: t/group\n            config: { k: 1 }\n        exports:\n          out: x\n"),
        Document("runtimes/container-app.yaml",
            "kind: Runtime\nname: container-app\ndescription: d\nimplementations:\n  default:\n"
            + "    probe: { path: /health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5 }\n"
            + "    release: { suffixInput: suffix, trafficInput: traffic, trafficOutput: traffic, suffixOutput: suffix, revisionOutput: revision, fqdnOutput: fqdn, revisionKey: revisionName, latestKey: latestRevision, weightKey: weight, resourceIdOutput: appId }\n"
            + "    nodes:\n      runtime:\n        template: t/runtime\n        config: { k: 1 }\n"),
        Document("templates/t/group/Pulumi.yaml", "name: group\n"),
    ]);

    private readonly FakeBackend _backend = new();
    private readonly List<string> _built = [];

    private static SourceDocument Document(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    // The backend factory and the templates factory record that they were called; the templates fit any graph unless given misfits.
    private Connections Connections(params LoadError[] misfits) => new(
        () => throw new NotSupportedException(),
        () => throw new NotSupportedException(),
        () => throw new NotSupportedException(),
        () =>
        {
            _built.Add("backend");
            return _backend;
        },
        () => new FakeProbe(_backend.Log),
        () => TimeProvider.System,
        part =>
        {
            _built.Add($"templates {part.Name} {string.Join(',', part.Documents.Select(d => d.Name))}");
            return new FakeTemplates(_backend, misfits);
        });

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

        var result = await DeployAsync(Connections(), progress: progress);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Null(result.Problem);
        Assert.Equal(["templates cat/templates t/group/Pulumi.yaml", "backend"], _built);
        Assert.Equal(["shop/dev/thing/group", "shop/dev/@workload/runtime"], result.Run?.Nodes.Select(n => n.NodeId));
        Assert.Equal([new WorkloadStarted("shop"), .. result.Run!.Nodes.Select(n => new NodeFinished(n))], progress);
    }

    [Fact]
    public async Task Deploy_refuses_a_gated_environment_with_nothing_built()
    {
        var progress = new List<Progress>();

        var result = await DeployAsync(Connections(), Environment.Replace("type: dev", "type: stage") + Approval, progress: progress);

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal(new Gated("dev"), result.Problem);
        Assert.Empty(result.Errors);
        Assert.Null(result.Run);
        Assert.Empty(_built);
        Assert.Empty(progress);
    }

    [Fact]
    public async Task Deploy_refuses_an_environment_type_the_catalog_does_not_declare_with_nothing_built()
    {
        var result = await DeployAsync(Connections(), Environment.Replace("type: dev", "type: prod"));

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Null(result.Problem);
        var error = Assert.Single(result.Errors);
        Assert.Equal((EnvironmentName, "type"), (error.File, error.Location));
        Assert.Contains("'prod'", error.Message, StringComparison.Ordinal);
        Assert.Null(result.Run);
        Assert.Empty(_built);
    }

    [Fact]
    public async Task A_preview_on_a_gated_environment_is_allowed_and_creates_nothing()
    {
        var result = await DeployAsync(Connections(), Environment.Replace("type: dev", "type: stage") + Approval, preview: true);

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

        var result = await DeployAsync(Connections(misfit, misfit));

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal([misfit], result.Errors);
        Assert.Null(result.Run);
        Assert.Equal(["templates cat/templates t/group/Pulumi.yaml"], _built);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task A_failed_node_makes_the_run_failed_with_the_failed_node_last()
    {
        _backend.FailOn = "shop.dev.thing.group";

        var result = await DeployAsync(Connections());

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Null(result.Problem);
        Assert.Equal(NodeOutcome.Failed, result.Run?.Nodes[^1].Outcome);
    }

    // Fits any graph unless given misfits, which every check repeats; the content is the backend's.
    private sealed class FakeTemplates(FakeBackend backend, IReadOnlyList<LoadError> misfits) : ITemplates
    {
        public string GetContent(string templateName) => backend.GetContent(templateName);

        public IReadOnlyList<LoadError> Check(ResolvedGraph graph) => misfits;
    }
}
