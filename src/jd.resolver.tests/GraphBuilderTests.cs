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
    private const string ComponentFile = "f1.yaml";

    private static readonly string[] BaseFiles =
    [
        "kind: Catalog\nversion: \"7\"\nenvironmentTypes: { dev: { layer: environment }, stage: { layer: environment, approval: true } }\n",
    ];

    private static readonly EnvironmentDescriptor Env =
        new("dev", "region", "dev", new Dictionary<string, string> { ["cosmos-account.id"] = "/acct" }, ["sqldb", "container-app"], null);

    // One component node as YAML; config is a flow mapping body, written as it would be in the catalog.
    private static string N(string name, string config, string kind = "create", string template = "")
    {
        var templateName = template.Length > 0 ? template : $"t/{name}";
        return $"  {name}:\n    template: {templateName}\n    kind: {kind}\n    config: {{ {config} }}\n";
    }

    private static string Id(string scope, string node) => $"shop/dev/{scope}/{node}";

    private static async Task<ResolvedGraph> BuildAsync(string nodes, string requires = "  - type: sqldb\n", params string[] policies)
    {
        var component = TestCatalog.Component("sqldb", "[endpoint]", $"nodes:\n{nodes}exports:\n  endpoint: x\n");
        var files = BaseFiles.Append(component).Concat(policies).Append(TestCatalog.RuntimeFile).Append(TestCatalog.GrantsFile).Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var loaded = await CatalogParser.ParseAsync(files);
        Assert.Empty(loaded.Errors);
        var workload = (JObject)YamlSchemaValidator.ParseYaml($"metadata: {{ name: shop, team: crew }}\nrequires:\n{requires}");
        var applied = new PolicyApplier(loaded.Value!, Env).Apply(new Expander(loaded.Value!, Env).Expand(workload, "workload.yaml"));
        return new GraphBuilder(Env, loaded.Value!.GrantTargets).Build(applied);
    }

    private static GraphNode Node(ResolvedGraph graph, string scope, string name) => graph.Nodes.Single(n => n.Id == Id(scope, name));

    [Fact]
    public async Task Ids_and_stack_names_are_per_workload_environment_scope_and_node()
    {
        var graph = await BuildAsync(
            N("n", "k: 1"),
            "  - type: sqldb\n    id: users\n  - type: sqldb\n    id: orders\n");

        Assert.Empty(graph.Errors);
        Assert.Equal(["7", "dev", "shop"], [graph.CatalogVersion, graph.Environment, graph.Workload]);
        var orders = Node(graph, "orders", "n");
        Assert.Equal(("orders", "n", "shop.dev.orders.n"), (orders.Scope, orders.Name, orders.Stack));
        Assert.Equal("shop.dev.users.n", Node(graph, "users", "n").Stack);
        var runtime = Node(graph, "@workload", "runtime");
        Assert.Equal(("@workload", "shop.dev._workload.runtime"), (runtime.Scope, runtime.Stack));
        Assert.Equal(3, graph.Nodes.Select(n => n.Stack).Distinct().Count());
        Assert.Equal(["orders", "users"], graph.Exports.Keys);
        Assert.Equal("x", Assert.IsType<Resolved>(graph.Exports["orders"]["endpoint"]).Value);
    }

    [Fact]
    public async Task Stacks_are_valid_backend_names_and_distinct_even_for_look_alike_ids()
    {
        var graph = await BuildAsync(
            N("c", "k: 1") + N("b-c", "k: 1"),
            "  - type: sqldb\n    id: a-b\n  - type: sqldb\n    id: a\n");

        Assert.Empty(graph.Errors);
        Assert.All(graph.Nodes, n => Assert.Matches("^[a-z0-9._-]+$", n.Stack));
        Assert.Equal(5, graph.Nodes.Select(n => n.Stack).Distinct().Count());
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
        var created = Hash(await BuildAsync(N("n", "scope: '${runtime.id}'", template: "t/g")));
        Assert.NotEqual(created, Hash(await BuildAsync(N("n", "scope: '${runtime.id}'", kind: "grant", template: "t/g"))));
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
    [InlineData("'${c.id}'")]
    [InlineData("'${ c.id }'")]
    [InlineData("'${runtime.principalId}'")]
    public async Task A_grant_targeting_its_own_node_or_the_runtime_is_accepted(string target)
    {
        var graph = await BuildAsync(N("c", "k: 1") + N("g", $"scope: {target}", kind: "grant", template: "t/g"));

        Assert.Empty(graph.Errors);
        Assert.Equal(3, graph.Nodes.Count);
    }

    // After policies the target is still one reference to the grant's own scope or the runtime (security boundary 1): a policy cannot
    // point it at a shared resource, a literal, text around a reference or a function.
    [Theory]
    [InlineData("'${cosmos-account.id}'")]
    [InlineData("/subscriptions/s/accounts/a")]
    [InlineData("'x-${c.id}'")]
    [InlineData("\"${guid(c.id)}\"")]
    public async Task A_policy_setting_a_grant_target_to_anything_but_its_own_node_is_refused_at_graph_build(string value)
    {
        var graph = await BuildAsync(
            N("c", "k: 1") + N("g", "scope: '${c.id}'", kind: "grant", template: "t/g"),
            "  - type: sqldb\n",
            $"kind: Policy\nname: pol\nreason: r\nmatch: {{ template: t/g }}\nset: {{ scope: {value} }}\n");

        var error = Assert.Single(graph.Errors);
        Assert.Equal(("f2.yaml", "scope"), (error.File, error.Location));
        Assert.Contains($"node '{Id("sqldb", "g")}'", error.Message);
        Assert.Contains("grant's target ('scope')", error.Message);
    }

    [Fact]
    public void A_grant_whose_template_has_no_target_or_whose_target_waits_on_another_node_is_refused_at_graph_build()
    {
        var target = new ConfigText(new Pending("${c.id}", new HashSet<Reference> { new(ReferenceKind.Node, "c", "id") }), new HashSet<string>());
        var grant = new ResolvedNode("g", "t/g", NodeKind.Grant, new Dictionary<string, ConfigValue> { ["scope"] = target }, new Dictionary<string, Provenance>());
        var create = new ResolvedNode("c", "t/c", NodeKind.Create, new Dictionary<string, ConfigValue>(), new Dictionary<string, Provenance>());
        var policy = new PolicyResult(
            "1", "shop", "crew", null, null, [new ResolvedRequirement("sqldb", "sqldb", "standard", "m.yaml", new Dictionary<string, EvalResult>(), [create, grant])],
            [], [], new Dictionary<string, IReadOnlyDictionary<string, string>>());

        Assert.Empty(new GraphBuilder(Env, new Dictionary<string, string> { ["t/g"] = "scope" }).Build(policy).Errors);
        var error = Assert.Single(new GraphBuilder(Env, new Dictionary<string, string>()).Build(policy).Errors);
        Assert.Equal((Id("sqldb", "g"), "template"), (error.File, error.Location));
        Assert.Contains("'t/g' is not a grant template", error.Message);

        // A part of the run read in place is written as one reference but waits on another part's node: not the grant's own.
        var inPlace = new ConfigText(new Pending("${shared-group.name}", new HashSet<Reference> { new(ReferenceKind.Node, "c", "id") }), new HashSet<string>());
        var reached = policy with { Requirements = [policy.Requirements[0] with { Nodes = [create, grant with { Config = new Dictionary<string, ConfigValue> { ["scope"] = inPlace } }] }] };
        Assert.Equal(["scope"], new GraphBuilder(Env, new Dictionary<string, string> { ["t/g"] = "scope" }).Build(reached).Errors.Select(e => e.Location));
    }

    [Fact]
    public async Task A_resource_reference_in_node_config_is_an_error()
    {
        var graph = await BuildAsync(N("a", "v: '${resource.other.endpoint}'"));

        var error = Assert.Single(graph.Errors);
        Assert.Equal((ComponentFile, "v"), (error.File, error.Location));
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
