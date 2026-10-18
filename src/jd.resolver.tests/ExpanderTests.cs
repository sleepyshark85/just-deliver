using jd.definitionvalidator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

public class ExpanderTests
{
    private static readonly string[] BaseFiles =
    [
        "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment }, stage: { layer: environment, approval: true } }\n",
        "kind: Naming\nrules:\n  thing:\n    pattern: \"{workload}-{id}\"\n    maxLength: 40\n    allowed: \"[a-z0-9-]\"\n",
        "kind: Roles\nroles:\n  reader: 00000000-0000-0000-0000-000000000001\n",
    ];

    private const string ComponentFile = "f3.yaml";

    private static string Sqldb(string nodes = "  n:\n    template: t/n\n    config: { a: 1 }\n", string exports = "  endpoint: ${n.out}\n", string key = "default") =>
        TestCatalog.Component("sqldb", "[endpoint]", $"nodes:\n{nodes}exports:\n{exports}", key);

    // A default implementation and one for stage, told apart by their endpoint.
    private const string SqldbForStage =
        "kind: Component\nname: sqldb\ndescription: d\nexports: [endpoint]\nimplementations:\n"
        + "  default:\n    classes:\n      standard: { nodes: { n: { template: t/n, config: {} } }, exports: { endpoint: plain } }\n"
        + "  stage:\n    classes:\n      standard: { nodes: { n: { template: t/n, config: {} } }, exports: { endpoint: guarded } }\n";

    private static EnvironmentDescriptor Env(string type = "dev") =>
        new("dev", "region", type, new Dictionary<string, string>(), ["sqldb", "container-app"], null);

    private static async Task<Catalog> LoadAsync(params string[] components)
    {
        var files = BaseFiles.Concat(components).Append(TestCatalog.RuntimeFile).Append(TestCatalog.GrantsFile).Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var result = await CatalogParser.ParseAsync(files);
        Assert.Empty(result.Errors);
        return result.Catalog!;
    }

    private static JObject Workload(string requires) =>
        (JObject)YamlSchemaValidator.ParseYaml($"metadata: {{ name: shop, team: crew }}\nrequires:\n{requires}");

    private static ExpansionResult Expand(Catalog catalog, string requires, string type = "dev") =>
        new Expander(catalog, Env(type)).Expand(Workload(requires), "workload.yaml");

    [Fact]
    public async Task A_requirement_expands_its_component_class_with_the_requirement_id()
    {
        var catalog = await LoadAsync(Sqldb(
            "  n:\n    template: t/g\n    kind: grant\n    config:\n      scope: ${runtime.id}\n      name: ${name('thing')}\n      host: ${env.region}\n",
            "  endpoint: ${env.region}\n"));

        var result = Expand(catalog, "  - type: sqldb\n    id: orders\n");

        Assert.Empty(result.Errors);
        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(("orders", "sqldb", "standard", ComponentFile), (requirement.Id, requirement.Type, requirement.Class, requirement.Source));
        var node = Assert.Single(requirement.Nodes);
        Assert.Equal(("n", "t/g", NodeKind.Grant), (node.Name, node.Template, node.Kind));
        Assert.Equal("shop-orders", Text(node.Config["name"]));
        Assert.Equal("region", Text(node.Config["host"]));
        Assert.Equal("region", Assert.IsType<Resolved>(requirement.Exports["endpoint"]).Value);
    }

    [Theory]
    [InlineData("stage", "guarded")]
    [InlineData("dev", "plain")]
    public async Task The_environment_types_own_implementation_is_used_else_the_default(string type, string endpoint)
    {
        var catalog = await LoadAsync(SqldbForStage);

        var requirement = Assert.Single(Expand(catalog, "  - type: sqldb\n", type).Requirements);

        Assert.Equal(endpoint, Assert.IsType<Resolved>(requirement.Exports["endpoint"]).Value);
    }

    [Fact]
    public async Task No_implementation_for_the_environment_type_and_no_default_is_an_error_naming_the_component_and_the_type()
    {
        var catalog = await LoadAsync(Sqldb(key: "stage"));

        var result = Expand(catalog, "  - type: sqldb\n    id: orders\n");

        var error = Assert.Single(result.Errors);
        Assert.Equal(("workload.yaml", "requires[0]"), (error.File, error.Location));
        Assert.Equal("requirement 'orders': component 'sqldb' has no implementation for environment type 'dev' and no 'default' implementation.", error.Message);
        Assert.Empty(result.Requirements);
    }

    [Fact]
    public async Task A_class_the_component_lacks_is_an_error_naming_the_component_the_class_and_the_environment_type()
    {
        var catalog = await LoadAsync(Sqldb());

        var result = Expand(catalog, "  - type: sqldb\n    id: orders\n    class: isolated\n");

        var error = Assert.Single(result.Errors);
        Assert.Equal(("workload.yaml", "requires[0]"), (error.File, error.Location));
        Assert.Equal("requirement 'orders': component 'sqldb' has no class 'isolated' for environment type 'dev' (classes: standard).", error.Message);
        Assert.Empty(result.Requirements);
    }

    [Fact]
    public async Task A_type_that_names_no_component_is_an_error()
    {
        var catalog = await LoadAsync(Sqldb());

        var error = Assert.Single(Expand(catalog, "  - type: cache\n").Errors);

        Assert.Equal(("requires[0]", "requirement 'cache': 'cache' is not a component in the catalog."), (error.Location, error.Message));
    }

    private const string RuntimeNodes = "nodes:\n  runtime:\n    template: t/rt\n    config: { image: '${workload.image}', port: '${workload.port}', name: \"${name('thing')}\" }\n";

    private const string RuntimeFile = "f3.yaml";

    private static JObject WorkloadWith(string container) =>
        (JObject)YamlSchemaValidator.ParseYaml($"metadata: {{ name: shop, team: crew }}\ncontainer:\n{container}requires: []\n");

    private static async Task<ExpansionResult> ExpandRuntimeAsync(string container, string type, params string[] runtimes) =>
        new Expander(await LoadWithoutRuntimeAsync(runtimes), Env(type)).Expand(WorkloadWith(container), "workload.yaml");

    private static async Task<Catalog> LoadWithoutRuntimeAsync(string[] files)
    {
        var result = await CatalogParser.ParseAsync(BaseFiles.Concat(files).Select((content, i) => new CatalogSource($"f{i}.yaml", content)));
        Assert.Empty(result.Errors);
        return result.Catalog!;
    }

    [Fact]
    public async Task A_workload_gets_the_runtime_nodes_with_image_port_and_probe()
    {
        var result = await ExpandRuntimeAsync("  image: reg.example/app:1\n  ports:\n    - port: 8080\n    - port: 9090\n", "dev",
            TestCatalog.Runtime(RuntimeNodes));

        Assert.Empty(result.Errors);
        var runtime = Assert.IsType<ExpandedRuntime>(result.Runtime);
        var node = Assert.Single(runtime.Nodes);
        Assert.Equal(("runtime", "t/rt", RuntimeFile), (node.Name, node.Template, runtime.Source));
        Assert.Equal("reg.example/app:1", Text(node.Config["image"]));
        Assert.Equal("8080", Text(node.Config["port"]));
        Assert.StartsWith("shop-", Text(node.Config["name"]));
        Assert.Equal((TestCatalog.Probe, TestCatalog.Release), (runtime.Probe, runtime.Release));
    }

    [Theory]
    [InlineData("stage", "t/guarded")]
    [InlineData("dev", "t/plain")]
    public async Task The_runtime_uses_the_environment_types_own_implementation_else_the_default(string type, string template)
    {
        var runtime = TestCatalog.Runtime(RuntimeNodes.Replace("t/rt", "t/plain")) + TestCatalog.RuntimeImplementation(RuntimeNodes.Replace("t/rt", "t/guarded"), "stage");

        var result = await ExpandRuntimeAsync("  image: i\n  ports:\n    - port: 80\n", type, runtime);

        Assert.Equal(template, Assert.Single(result.Runtime!.Nodes).Template);
    }

    // Boundary 11: a requirement's component must be among the environment's offers; the other requirements still expand.
    [Fact]
    public async Task A_requirement_whose_component_the_environment_does_not_offer_is_an_error_naming_both()
    {
        var catalog = await LoadAsync(Sqldb(), Sqldb().Replace("name: sqldb", "name: cache", StringComparison.Ordinal));

        var result = new Expander(catalog, Env() with { Offers = ["cache", "container-app"] }).Expand(Workload("  - type: sqldb\n  - type: cache\n"), "workload.yaml");

        var error = Assert.Single(result.Errors);
        Assert.Equal(("workload.yaml", "requires[0]", "requirement 'sqldb': environment 'dev' does not offer sqldb (offers: cache, container-app)."), (error.File, error.Location, error.Message));
        Assert.Equal(["cache"], result.Requirements.Select(r => r.Id));
    }

    // The runtime is the environment's offered one, not the catalog's (docs/architecture/components.md section 3).
    [Theory]
    [InlineData("", "environment 'dev' offers no runtime, so no workload can deploy on it.")]
    [InlineData("sqldb", "environment 'dev' offers no runtime, so no workload can deploy on it.")]
    [InlineData("container-app,other-app", "environment 'dev' offers more than one runtime (container-app, other-app); a workload runs on exactly one.")]
    public async Task A_workload_needs_exactly_one_offered_runtime(string offers, string message)
    {
        var result = await ExpandOfferingAsync(offers.Split(',', StringSplitOptions.RemoveEmptyEntries));

        var error = Assert.Single(result.Errors);
        Assert.Null(result.Runtime);
        Assert.Equal(("workload.yaml", "runtime", message), (error.File, error.Location, error.Message));
    }

    [Fact]
    public async Task A_workload_gets_the_runtime_its_environment_offers_among_those_the_catalog_declares()
    {
        var result = await ExpandOfferingAsync(["other-app"]);

        Assert.Empty(result.Errors);
        Assert.Equal(("t/other", $"f{BaseFiles.Length + 1}.yaml"), (Assert.Single(result.Runtime!.Nodes).Template, result.Runtime.Source));
    }

    private static async Task<ExpansionResult> ExpandOfferingAsync(string[] offers)
    {
        var catalog = await LoadWithoutRuntimeAsync([TestCatalog.Runtime(RuntimeNodes), TestCatalog.Runtime(RuntimeNodes.Replace("t/rt", "t/other")).Replace("name: container-app", "name: other-app")]);
        return new Expander(catalog, Env() with { Offers = offers }).Expand(WorkloadWith("  image: i\n  ports:\n    - port: 80\n"), "workload.yaml");
    }

    [Fact]
    public async Task No_runtime_implementation_for_the_environment_type_and_no_default_is_an_error_naming_the_runtime_and_the_type()
    {
        var result = await ExpandRuntimeAsync("  image: i\n  ports:\n    - port: 80\n", "dev", TestCatalog.Runtime(RuntimeNodes, "stage"));

        var error = Assert.Single(result.Errors);
        Assert.Null(result.Runtime);
        Assert.Equal("runtime 'container-app' has no implementation for environment type 'dev' and no 'default' implementation.", error.Message);
    }

    [Fact]
    public async Task Reading_the_port_of_a_workload_without_ports_is_an_error_naming_the_workload()
    {
        var result = await ExpandRuntimeAsync("  image: i\n", "dev", TestCatalog.Runtime(RuntimeNodes));

        var error = Assert.Single(result.Errors);
        Assert.Null(result.Runtime);
        Assert.Equal((RuntimeFile, "implementations.default.nodes.runtime.config.port"), (error.File, error.Location));
        Assert.Contains("for the runtime: ", error.Message);
        Assert.Contains("workload 'shop' declares no port", error.Message);
    }

    [Fact]
    public async Task Config_is_walked_through_objects_and_arrays_and_scalars_pass_through()
    {
        var catalog = await LoadAsync(Sqldb("  n:\n    template: t/n\n    config:\n      size: 400\n      on: true\n      none: null\n      tags: [x, '${env.region}', 7]\n      nested: { deep: { host: '${env.region}' }, count: 2 }\n"));

        var node = Assert.Single(Assert.Single(Expand(catalog, "  - type: sqldb\n").Requirements).Nodes);

        Assert.Equal(400, (int)Assert.IsType<ConfigScalar>(node.Config["size"]).Value);
        Assert.True((bool)Assert.IsType<ConfigScalar>(node.Config["on"]).Value);
        Assert.Equal(JTokenType.Null, Assert.IsType<ConfigScalar>(node.Config["none"]).Value.Type);
        var tags = Assert.IsType<ConfigArray>(node.Config["tags"]).Items;
        Assert.Equal(("x", "region"), (Text(tags[0]), Text(tags[1])));
        Assert.Equal(7, (int)Assert.IsType<ConfigScalar>(tags[2]).Value);
        var nested = Assert.IsType<ConfigObject>(node.Config["nested"]).Properties;
        Assert.Equal("region", Text(Assert.IsType<ConfigObject>(nested["deep"]).Properties["host"]));
        Assert.Equal(2, (int)Assert.IsType<ConfigScalar>(nested["count"]).Value);
    }

    [Fact]
    public async Task Pending_values_carry_their_references()
    {
        var catalog = await LoadAsync(Sqldb("  n:\n    template: t/n\n    config: { a: 1 }\n  m:\n    template: t/m\n    config:\n      db: ${n.databaseName}\n      who: [{ id: '${runtime.principalId}' }]\n",
            "  endpoint: ${n.endpoint}\n"));

        var requirement = Assert.Single(Expand(catalog, "  - type: sqldb\n").Requirements);

        var config = requirement.Nodes.Single(n => n.Name == "m").Config;
        Assert.Equal(new Reference(ReferenceKind.Node, "n", "databaseName"), Assert.Single(Assert.IsType<Pending>(Assert.IsType<ConfigText>(config["db"]).Result).References));
        var item = Assert.IsType<ConfigObject>(Assert.Single(Assert.IsType<ConfigArray>(config["who"]).Items));
        Assert.Equal(new Reference(ReferenceKind.Node, "runtime", "principalId"), Assert.Single(Assert.IsType<Pending>(Assert.IsType<ConfigText>(item.Properties["id"]).Result).References));
        Assert.Equal("${n.endpoint}", Assert.IsType<Pending>(requirement.Exports["endpoint"]).Original);
    }

    [Fact]
    public async Task Expression_errors_are_collected_across_requirements_with_the_mapping_file_and_requirement()
    {
        var catalog = await LoadAsync(Sqldb("  n:\n    template: t/n\n    config: { a: '${workload.nope}', b: '${role.nope}' }\n"));

        var result = Expand(catalog, "  - type: sqldb\n    id: one\n  - type: sqldb\n    id: two\n");

        Assert.Equal(4, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Equal(ComponentFile, e.File));
        Assert.Contains(result.Errors, e => e.Location == $"{TestCatalog.PartPath}.nodes.n.config.b" && e.Message.StartsWith("for requires[1] ('two')"));
        Assert.Empty(result.Requirements);
    }

    [Fact]
    public async Task The_loaded_catalog_is_not_changed_by_expansion()
    {
        var catalog = await LoadAsync(Sqldb("  n:\n    template: t/n\n    config: { size: 400, tags: [1], nested: { k: v } }\n"));
        string Snapshot() => JsonConvert.SerializeObject(catalog.Components);
        var before = Snapshot();

        var node = Assert.Single(Assert.Single(Expand(catalog, "  - type: sqldb\n").Requirements).Nodes);
        ((JValue)Assert.IsType<ConfigScalar>(node.Config["size"]).Value).Value = 999;
        ((JValue)Assert.IsType<ConfigScalar>(Assert.IsType<ConfigArray>(node.Config["tags"]).Items[0]).Value).Value = 999;

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task Seed_catalog_and_sample_workload_expand_without_errors()
    {
        var catalog = (await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog"))).Catalog!;
        var workload = (JObject)YamlSchemaValidator.ParseYaml(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml")));
        var environment = new EnvironmentDescriptor(
            "dev",
            "region",
            "dev",
            new Dictionary<string, string>
            {
                ["env-group.name"] = "rg",
                ["apps-env.id"] = "/cae",
                ["cosmos-account.name"] = "acct",
                ["database.databaseName"] = "db",
                ["cosmos-account.id"] = "/acct",
                ["cosmos-account.endpoint"] = "https://acct",
                ["log-analytics.id"] = "/law",
            },
            ["container-app", "database", "monitoring"],
            null);

        var result = new Expander(catalog, environment).Expand(workload, "workload.yaml");

        Assert.Empty(result.Errors);
        Assert.Equal(["database", "monitoring"], result.Requirements.Select(r => r.Type));
        var requirement = result.Requirements[0];
        Assert.Equal(["access", "container"], requirement.Nodes.Select(n => n.Name).Order(StringComparer.Ordinal));
        var runtime = Assert.IsType<ExpandedRuntime>(result.Runtime);
        Assert.Equal(("runtime", "azure/container-app"), (Assert.Single(runtime.Nodes).Name, runtime.Nodes[0].Template));
        Assert.Equal(("8080", "/health", 200), (Text(runtime.Nodes[0].Config["targetPort"]), runtime.Probe!.Path, runtime.Probe.ExpectedStatus));

        // Boundary 7: only the requirements' class parts and the runtime, never a shared resource or an environment part (the database
        // node, the groups, the account), although the descriptor holds their values.
        Assert.Equal(["access", "access", "appinsights", "container", "runtime"], result.Requirements.SelectMany(r => r.Nodes).Concat(runtime.Nodes).Select(n => n.Name).Order(StringComparer.Ordinal));
        Assert.Empty(result.Parts);
        var access = requirement.Nodes.Single(n => n.Name == "access");
        Assert.Equal(NodeKind.Grant, access.Kind);
        Assert.Equal(["container", "database", "endpoint", "engine"], requirement.Exports.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new Resolved("cosmos-sql"), requirement.Exports["engine"]);
        Assert.IsType<Pending>(Assert.IsType<ConfigText>(access.Config["principalId"]).Result);
    }

    private static string Text(ConfigValue value) => Assert.IsType<Resolved>(Assert.IsType<ConfigText>(value).Result).Value;
}
