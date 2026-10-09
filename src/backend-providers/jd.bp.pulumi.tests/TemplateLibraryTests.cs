using System.Text.RegularExpressions;
using jd.definitionvalidator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using jd.resolver.policies;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.bp.pulumi.tests;

public partial class TemplateLibraryTests
{
    private static readonly string Templates = Path.Combine(AppContext.BaseDirectory, "catalog", "templates");

    // Small library: "t/a" needs "name" and takes an optional "size"; "t/b" outputs "id".
    private static readonly TemplateLibrary Library = TemplateLibrary.Parse(
    [
        ("t/a", "name: a\nconfiguration:\n  name:\n    type: String\n  size:\n    type: Integer\n    default: 1\noutputs:\n  id: ${r.id}\n"),
        ("t/b", "name: b\nconfiguration:\n  other:\n    type: String\noutputs:\n  id: ${r.id}\n"),
    ]);

    private static ConfigText Text(string value) => new(new Resolved(value), new HashSet<string>());

    private static ConfigText Ref(string node, string output) =>
        new(new Pending($"${{{node}.{output}}}", new HashSet<Reference> { new(ReferenceKind.Node, node, output) }), new HashSet<string>());

    private static GraphNode Node(string name, string template, Dictionary<string, ConfigValue> config) =>
        new($"w/dev/s/{name}", "s", name, template, NodeKind.Create, $"w.dev.s.{name}", Phase.Infrastructure,
            config, new Dictionary<string, Provenance>(), "hash", []);

    private static ResolvedGraph Graph(IEnumerable<GraphNode> nodes, IReadOnlyDictionary<string, IReadOnlyDictionary<string, EvalResult>>? exports = null) =>
        new("1", "dev", "w", "crew", nodes.ToList(), exports ?? new Dictionary<string, IReadOnlyDictionary<string, EvalResult>>(), []);

    [Fact]
    public async Task The_seed_catalog_and_sample_workload_fit_the_template_library()
    {
        var catalog = (await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog"))).Catalog!;
        var environment = (await EnvironmentFile.LoadAsync(Path.Combine(AppContext.BaseDirectory, "golden", "seed-environment.yaml"))).Descriptor!;
        var workload = (JObject)YamlSchemaValidator.ParseYaml(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml")));
        var graph = new GraphBuilder(environment).Build(new PolicyApplier(catalog, environment).Apply(new Expander(catalog, environment).Expand(workload, "workload.yaml")));
        var library = await TemplateLibrary.LoadAsync(Templates);

        Assert.Empty(graph.Errors);
        Assert.Empty(library.Check(graph));
    }

    [Fact]
    public async Task Seed_templates_have_no_defaults_and_no_fixed_guids()
    {
        var library = await TemplateLibrary.LoadAsync(Templates);

        Assert.Empty(library.Errors);
        foreach (var file in Directory.EnumerateFiles(Templates, "Pulumi.yaml", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotMatch(@"(?m)^\s+default:", text);
            Assert.DoesNotMatch(Guid(), text);
        }
    }

    [Fact]
    public async Task The_container_app_template_declares_its_inputs_and_outputs()
    {
        var library = await TemplateLibrary.LoadAsync(Templates);
        string[] inputs = ["resourceGroupName", "location", "containerAppsEnvironmentId", "containerAppName", "image", "targetPort", "externalIngress", "cpu", "memory", "minReplicas", "maxReplicas", "variables"];
        string[] outputs = ["containerAppId", "principalId", "latestRevisionName", "latestRevisionFqdn", "fqdn"];
        var app = Node("app", "azure/container-app", inputs.ToDictionary(i => i, i => (ConfigValue)Text("x")));
        // One consumer per output: a reference to an output the template does not declare is an error.
        var consumers = outputs.Select(o => Node("c-" + o, "azure/resource-group", new() { ["resourceGroupName"] = Ref("app", o), ["location"] = Text("x"), ["tags"] = Text("x") }));
        var incomplete = Node("incomplete", "azure/container-app", inputs.Skip(1).ToDictionary(i => i, i => (ConfigValue)Text("x")));

        Assert.Empty(library.Check(Graph([app, .. consumers])));
        var error = Assert.Single(library.Check(Graph([incomplete])));
        Assert.Equal(("w/dev/s/incomplete", "resourceGroupName"), (error.File, error.Location));
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex Guid();

    [Fact]
    public void A_node_that_fits_its_template_passes()
    {
        var graph = Graph([Node("x", "t/a", new() { ["name"] = Text("n") }), Node("y", "t/b", new() { ["other"] = Ref("x", "id") })]);

        Assert.Empty(Library.Check(graph));
    }

    [Fact]
    public void An_unknown_template_is_an_error()
    {
        var error = Assert.Single(Library.Check(Graph([Node("x", "t/missing", [])])));

        Assert.Equal(("w/dev/s/x", "template"), (error.File, error.Location));
        Assert.Contains("'t/missing'", error.Message);
    }

    [Fact]
    public void A_config_key_the_template_does_not_declare_is_an_error()
    {
        var error = Assert.Single(Library.Check(Graph([Node("x", "t/a", new() { ["name"] = Text("n"), ["sku"] = Text("F1") })])));

        Assert.Equal(("w/dev/s/x", "sku"), (error.File, error.Location));
        Assert.Contains("'t/a'", error.Message);
    }

    [Fact]
    public void A_required_input_the_node_does_not_set_is_an_error_but_an_input_with_a_default_may_be_left_out()
    {
        var error = Assert.Single(Library.Check(Graph([Node("x", "t/a", [])])));

        Assert.Equal(("w/dev/s/x", "name"), (error.File, error.Location));
        Assert.Contains("requires", error.Message);
    }

    [Fact]
    public void A_reference_to_an_output_the_template_does_not_declare_is_an_error()
    {
        var graph = Graph([Node("x", "t/a", new() { ["name"] = Text("n") }), Node("y", "t/b", new() { ["other"] = Ref("x", "endpoint") })]);

        var error = Assert.Single(Library.Check(graph));

        Assert.Equal(("w/dev/s/y", "other"), (error.File, error.Location));
        Assert.Contains("x.endpoint", error.Message);
        Assert.Contains("'t/a'", error.Message);
    }

    [Fact]
    public void A_reference_inside_a_nested_value_is_checked()
    {
        var nested = new ConfigObject(new Dictionary<string, ConfigValue> { ["list"] = new ConfigArray([Ref("x", "nope")]) });

        var error = Assert.Single(Library.Check(Graph([Node("x", "t/a", new() { ["name"] = Text("n") }), Node("y", "t/b", new() { ["other"] = nested })])));

        Assert.Equal("other", error.Location);
    }

    [Fact]
    public void An_export_that_references_an_undeclared_output_is_an_error()
    {
        var exports = new Dictionary<string, IReadOnlyDictionary<string, EvalResult>>
        {
            ["s"] = new Dictionary<string, EvalResult> { ["ok"] = Ref("x", "id").Result, ["bad"] = Ref("x", "nope").Result },
        };

        var error = Assert.Single(Library.Check(Graph([Node("x", "t/a", new() { ["name"] = Text("n") })], exports)));

        Assert.Equal(("w/dev/s", "exports.bad"), (error.File, error.Location));
    }

    [Fact]
    public void A_runtime_reference_is_not_checked_against_a_template()
    {
        var graph = Graph([Node("x", "t/a", new() { ["name"] = Ref(ExpressionEvaluator.RuntimeNode, "principalId") })]);

        Assert.Empty(Library.Check(graph));
    }

    [Fact]
    public void Every_problem_is_reported_not_only_the_first()
    {
        var errors = Library.Check(Graph([Node("x", "t/a", new() { ["sku"] = Text("F1") }), Node("y", "t/missing", [])]));

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void An_unreadable_template_is_reported_with_its_file_and_repeated_by_the_check()
    {
        var library = TemplateLibrary.Parse([("t/broken", "name: [unclosed\n")]);

        Assert.Equal("t/broken/Pulumi.yaml", Assert.Single(library.Errors).File);
        Assert.Single(library.Check(Graph([])));
    }

    [Fact]
    public async Task A_missing_template_directory_is_an_error()
    {
        var library = await TemplateLibrary.LoadAsync(Path.Combine(AppContext.BaseDirectory, "no-such-templates"));

        Assert.Contains("not found", Assert.Single(library.Errors).Message);
    }
}
