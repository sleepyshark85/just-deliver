using System.Security.Cryptography;
using System.Text;
using jd.definitionvalidator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using jd.resolver.policies;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

public class GraphBuilderTests
{
    private const string MappingFile = "f3.yaml";

    private static readonly string[] BaseFiles =
    [
        "kind: Catalog\nversion: \"7\"\n",
        "kind: ResourceType\nname: sqldb\ndescription: d\nclasses: [standard]\nexports: [endpoint]\n",
        "kind: ResourceType\nname: other\ndescription: d\nclasses: [standard]\nexports: [endpoint]\n",
    ];

    private static readonly EnvironmentDescriptor Env =
        new("dev", "region", "team", new Dictionary<string, string> { ["host"] = "h1", ["grant.ok"] = "g1" }, ["grant.ok"]);

    // One mapping node as YAML; config is a flow mapping body, written as it would be in the catalog.
    private static string N(string name, string config, string kind = "create", string template = "")
    {
        var templateName = template.Length > 0 ? template : $"t/{name}";
        return $"  {name}:\n    template: {templateName}\n    kind: {kind}\n    config: {{ {config} }}\n";
    }

    private static string Id(string scope, string node) => $"shop/dev/{scope}/{node}";

    private static async Task<ResolvedGraph> BuildAsync(string nodes, string requires = "  - type: sqldb\n", params string[] policies)
    {
        var mapping = $"kind: Mapping\nmatch: {{ type: sqldb }}\nnodes:\n{nodes}exports:\n  endpoint: x\n";
        var files = BaseFiles.Append(mapping).Concat(policies).Append(TestCatalog.RuntimeMapping).Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var loaded = await CatalogParser.ParseAsync(files);
        Assert.Empty(loaded.Errors);
        var workload = (JObject)YamlSchemaValidator.ParseYaml($"metadata: {{ name: shop, team: crew }}\nrequires:\n{requires}");
        var applied = new PolicyApplier(loaded.Catalog!, Env).Apply(new Expander(loaded.Catalog!, Env).Expand(workload, "workload.yaml"));
        return new GraphBuilder(Env).Build(applied);
    }

    private static GraphNode Node(ResolvedGraph graph, string scope, string name) => graph.Nodes.Single(n => n.Id == Id(scope, name));

    [Fact]
    public async Task Ids_and_stack_names_are_per_workload_environment_scope_and_node()
    {
        var graph = await BuildAsync(
            N("n", "k: 1"),
            "  - type: sqldb\n    id: users\n  - type: sqldb\n    id: orders\n",
            "kind: Policy\nname: pol\nreason: r\nmatch: { kind: runtime }\nadd:\n  extra:\n    template: t/extra\n    config: {}\n");

        Assert.Empty(graph.Errors);
        Assert.Equal(["7", "dev", "shop"], [graph.CatalogVersion, graph.Environment, graph.Workload]);
        var orders = Node(graph, "orders", "n");
        Assert.Equal(("orders", "n", "shop.dev.orders.n"), (orders.Scope, orders.Name, orders.Stack));
        Assert.Equal("shop.dev.users.n", Node(graph, "users", "n").Stack);
        var extra = Node(graph, "@workload", "extra");
        Assert.Equal(("@workload", "shop.dev._workload.extra"), (extra.Scope, extra.Stack));
        Assert.Equal(4, graph.Nodes.Select(n => n.Stack).Distinct().Count());
        Assert.Equal(["orders", "users"], graph.Exports.Keys);
        Assert.Equal("x", Assert.IsType<Resolved>(graph.Exports["orders"]["endpoint"]).Value);
    }

    [Fact]
    public async Task Stacks_are_valid_backend_names_and_distinct_even_for_look_alike_ids()
    {
        var graph = await BuildAsync(
            N("c", "k: 1") + N("b-c", "k: 1"),
            "  - type: sqldb\n    id: a-b\n  - type: sqldb\n    id: a\n",
            "kind: Policy\nname: pol\nreason: r\nmatch: { kind: runtime }\nadd:\n  extra:\n    template: t/extra\n    config: {}\n");

        Assert.Empty(graph.Errors);
        Assert.All(graph.Nodes, n => Assert.Matches("^[a-z0-9._-]+$", n.Stack));
        Assert.Equal(6, graph.Nodes.Select(n => n.Stack).Distinct().Count());
    }

    [Fact]
    public async Task References_to_nodes_in_the_same_scope_become_edges()
    {
        var graph = await BuildAsync(
            N("a", "k: 1") + N("b", "k: 1") + N("c", "nested: { v: '${a.out}' }, list: ['${b.out}', '${a.out}']"),
            "  - type: sqldb\n    id: one\n  - type: sqldb\n    id: two\n");

        Assert.Empty(graph.Errors);
        Assert.Equal([Id("one", "a"), Id("one", "b")], Node(graph, "one", "c").DependsOn);
        Assert.Equal([Id("two", "a"), Id("two", "b")], Node(graph, "two", "c").DependsOn);
        Assert.Empty(Node(graph, "one", "a").DependsOn);
    }

    [Fact]
    public async Task A_runtime_reference_is_an_edge_to_the_runtime_node_and_puts_the_node_and_its_dependents_after_it()
    {
        var graph = await BuildAsync(N("a", "who: '${runtime.principalId}'") + N("b", "v: '${a.out}'") + N("c", "k: 1"));

        var runtime = Node(graph, "@workload", "runtime");
        var a = Node(graph, "sqldb", "a");
        var b = Node(graph, "sqldb", "b");
        var c = Node(graph, "sqldb", "c");
        Assert.Empty(graph.Errors);
        Assert.Equal(Phase.Runtime, runtime.Phase);
        Assert.Equal(Phase.AfterRuntime, a.Phase);
        Assert.Equal(Phase.AfterRuntime, b.Phase);
        Assert.Equal(Phase.Infrastructure, c.Phase);
        Assert.Equal([runtime.Id], a.DependsOn);
        Assert.True(graph.Nodes.ToList().IndexOf(runtime) < graph.Nodes.ToList().IndexOf(a));
    }

    [Fact]
    public async Task The_runtime_node_carries_the_probe_and_release_and_no_other_node_does()
    {
        var graph = await BuildAsync(N("a", "k: 1"));

        Assert.Equal((TestCatalog.Probe, TestCatalog.Release), (Node(graph, "@workload", "runtime").Probe, Node(graph, "@workload", "runtime").Release));
        Assert.All(graph.Nodes.Where(n => n.Name != "runtime"), n => Assert.Equal((null, null), (n.Probe, n.Release)));
    }

    [Fact]
    public async Task A_runtime_reference_in_an_environment_definition_is_an_error_because_it_has_no_runtime()
    {
        var mapping = $"kind: Mapping\nmatch: {{ type: sqldb }}\nnodes:\n{N("a", "who: '${runtime.principalId}'")}exports:\n  endpoint: x\n";
        var loaded = await CatalogParser.ParseAsync(BaseFiles.Append(mapping).Select((content, i) => new CatalogSource($"f{i}.yaml", content)));
        Assert.Empty(loaded.Errors);
        var expansion = new Expander(loaded.Catalog!, Env).ExpandEnvironment("dev", (JArray)YamlSchemaValidator.ParseYaml("- type: sqldb\n"), "definition.yaml");

        var graph = new GraphBuilder(Env).Build(new PolicyApplier(loaded.Catalog!, Env).Apply(expansion));

        var error = Assert.Single(graph.Errors);
        Assert.Equal((MappingFile, "who"), (error.File, error.Location));
        Assert.Contains("references runtime.principalId, but there is no runtime", error.Message);
    }

    [Fact]
    public async Task Order_is_topological_with_ties_broken_by_id_whatever_the_declaration_order()
    {
        var forward = await BuildAsync(N("z", "k: 1") + N("a", "k: 1") + N("m", "k: 1") + N("b", "v: '${z.out}'"));
        var reversed = await BuildAsync(N("b", "v: '${z.out}'") + N("m", "k: 1") + N("a", "k: 1") + N("z", "k: 1"));

        string[] expected = [Id("@workload", "runtime"), Id("sqldb", "a"), Id("sqldb", "m"), Id("sqldb", "z"), Id("sqldb", "b")];
        Assert.Equal(expected, forward.Nodes.Select(n => n.Id));
        Assert.Equal(expected, reversed.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task A_cycle_is_an_error_naming_only_the_nodes_in_it()
    {
        var graph = await BuildAsync(N("a", "v: '${b.out}'") + N("b", "v: '${a.out}'") + N("c", "v: '${a.out}'") + N("d", "k: 1"));

        var error = Assert.Single(graph.Errors);
        Assert.Contains($"dependency cycle: {Id("sqldb", "a")} -> {Id("sqldb", "b")} -> {Id("sqldb", "a")}", error.Message);
        Assert.DoesNotContain(Id("sqldb", "c"), error.Message);
    }

    [Fact]
    public async Task A_node_referencing_itself_is_a_cycle()
    {
        var graph = await BuildAsync(N("a", "v: '${a.out}'"));

        Assert.Contains($"{Id("sqldb", "a")} -> {Id("sqldb", "a")}", Assert.Single(graph.Errors).Message);
    }

    [Fact]
    public async Task The_hash_covers_template_kind_and_canonical_config()
    {
        var graph = await BuildAsync(N("n", "size: 400, ref: '${m.out}', nested: { b: 2, a: 1 }") + N("m", "k: 1"));

        const string canonical = """{"template":"t/n","kind":"create","config":{"nested":{"a":1,"b":2},"ref":"${m.out}","size":400}}""";
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), Node(graph, "sqldb", "n").Hash);
    }

    [Fact]
    public async Task The_hash_is_stable_across_runs_and_key_order_and_changes_with_config_template_or_kind()
    {
        string Hash(ResolvedGraph graph) => Node(graph, "sqldb", "n").Hash;
        var baseline = Hash(await BuildAsync(N("n", "a: 1, b: x")));

        Assert.Equal(baseline, Hash(await BuildAsync(N("n", "a: 1, b: x"))));
        Assert.Equal(baseline, Hash(await BuildAsync(N("n", "b: x, a: 1"))));
        Assert.NotEqual(baseline, Hash(await BuildAsync(N("n", "a: 2, b: x"))));
        Assert.NotEqual(baseline, Hash(await BuildAsync(N("n", "a: 1, b: x", template: "t/other"))));
        Assert.NotEqual(baseline, Hash(await BuildAsync(N("n", "a: 1, b: x", kind: "grant"))));
    }

    [Fact]
    public async Task Date_like_config_strings_are_kept_as_written_so_the_hash_does_not_depend_on_the_time_zone()
    {
        var graph = await BuildAsync(N("n", "since: 2024-05-01T10:00:00+07:00"));

        var node = Node(graph, "sqldb", "n");
        Assert.Equal("2024-05-01T10:00:00+07:00", (string?)ConfigJson.ToToken(node.Config["since"]));
        const string canonical = """{"template":"t/n","kind":"create","config":{"since":"2024-05-01T10:00:00+07:00"}}""";
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), node.Hash);
    }

    [Theory]
    [InlineData("v: '${env.host}'", "v")]
    [InlineData("nested: { v: 'a-${env.host}' }", "nested.v")]
    [InlineData("v: \"${guid(env.host, 'x')}\"", "v")]
    [InlineData("v: \"${guid(env.host, runtime.principalId)}\"", "v")]
    public async Task A_grant_using_an_environment_path_that_is_not_grantable_is_an_error(string config, string field)
    {
        var graph = await BuildAsync(N("g", config, kind: "grant"));

        var error = Assert.Single(graph.Errors);
        Assert.Equal((MappingFile, field), (error.File, error.Location));
        Assert.Contains($"node '{Id("sqldb", "g")}'", error.Message);
        Assert.Contains("env.host", error.Message);
        Assert.Contains("grantable", error.Message);
    }

    [Fact]
    public async Task A_grant_using_a_grantable_path_and_other_nodes_using_any_path_are_accepted()
    {
        var graph = await BuildAsync(
            N("g", "v: '${env.grant.ok}', w: \"${guid(env.grant.ok, 'x')}\"", kind: "grant") + N("c", "v: '${env.host}'"));

        Assert.Empty(graph.Errors);
        Assert.Equal(3, graph.Nodes.Count);
    }

    [Fact]
    public async Task The_grant_check_sees_paths_a_policy_adds_and_names_the_policy_file()
    {
        var graph = await BuildAsync(
            N("g", "k: 1", kind: "grant", template: "t/g"),
            "  - type: sqldb\n",
            "kind: Policy\nname: pol\nreason: r\nmatch: { template: t/g }\nset: { extra: '${env.host}' }\n");

        var error = Assert.Single(graph.Errors);
        Assert.Equal(("f4.yaml", "extra"), (error.File, error.Location));
    }

    [Fact]
    public async Task A_resource_reference_in_node_config_is_an_error()
    {
        var graph = await BuildAsync(N("a", "v: '${resource.other.endpoint}'"));

        var error = Assert.Single(graph.Errors);
        Assert.Equal((MappingFile, "v"), (error.File, error.Location));
        Assert.Contains("resource.other.endpoint", error.Message);
        Assert.Contains(Id("sqldb", "a"), error.Message);
    }

    [Fact]
    public async Task Errors_from_the_policy_step_are_carried()
    {
        var graph = await BuildAsync(N("a", "k: 1"), "  - type: other\n");

        var error = Assert.Single(graph.Errors);
        Assert.Contains("other", error.Message);
        Assert.Equal([Id("@workload", "runtime")], graph.Nodes.Select(n => n.Id));
    }
}
