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

// The workload's variables: fn::entries in the runtime mapping, ${resource.*} in a variable, and workload-scope policies on runtime.*.
public class RuntimeVariablesTests
{
    private static readonly EnvironmentDescriptor Env =
        new("dev", "region", "team", new Dictionary<string, string> { ["host"] = "h1" }, []);

    private const string RuntimeMapping = """
        kind: Mapping
        match: { kind: runtime }
        nodes:
          runtime:
            template: t/runtime
            config:
              image: ${workload.image}
              variables:
                fn::entries: workload.variables
        """;

    // A static export (endpoint), one waiting on a node (dyn), and a node that reads another requirement's export (the D19 case).
    private static string Mapping(string extraConfig = "") => $$"""
        kind: Mapping
        match: { type: sqldb }
        nodes:
          n:
            template: t/n
            config: { k: 1{{extraConfig}} }
        exports:
          endpoint: ${env.host}
          dyn: ${n.out}
        """;

    private static async Task<ResolvedGraph> ResolveAsync(string variables, string[]? policies = null, string runtimeMapping = RuntimeMapping, string mapping = "")
    {
        var files = new[]
            {
                "kind: Catalog\nversion: \"1\"\n",
                "kind: ResourceType\nname: sqldb\ndescription: d\nclasses: [standard]\nexports: [endpoint, dyn]\n",
                "kind: Naming\nrules:\n  thing:\n    pattern: \"{workload}-{id}\"\n    maxLength: 40\n    allowed: \"[a-z0-9-]\"\n",
                mapping.Length > 0 ? mapping : Mapping(),
                runtimeMapping,
            }
            .Concat(policies ?? [])
            .Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var loaded = await CatalogParser.ParseAsync(files);
        Assert.Empty(loaded.Errors);
        var workload = (JObject)YamlSchemaValidator.ParseYaml(
            $"metadata: {{ name: shop, team: crew }}\ncontainer:\n  image: reg/app:1\n  ports:\n    - port: 8080\n  variables:\n{variables}requires:\n  - type: sqldb\n");
        return Resolver.Resolve(workload, "workload.yaml", loaded.Catalog!, Env);
    }

    private static string WorkloadPolicy(string name, string body) =>
        $"kind: Policy\nname: {name}\nreason: r\nmatch: {{ kind: runtime }}\n{body}";

    private static GraphNode Runtime(ResolvedGraph graph) => graph.Nodes.Single(n => n.Name == "runtime");

    private static ConfigObject Variables(GraphNode node) => Assert.IsType<ConfigObject>(node.Config["variables"]);

    private static string Value(ConfigValue value) => Assert.IsType<Resolved>(Assert.IsType<ConfigText>(value).Result).Value;

    [Fact]
    public async Task The_workload_variables_are_a_map_in_the_graph_and_a_list_sorted_by_name_in_its_canonical_form()
    {
        var graph = await ResolveAsync("    ZED: z\n    alpha: a\n    BETA: b\n");

        Assert.Empty(graph.Errors);
        var variables = Variables(Runtime(graph));
        Assert.True(variables.AsEntries);
        Assert.Equal(["BETA", "ZED", "alpha"], variables.Properties.Keys.Order(StringComparer.Ordinal));
        var list = (JArray)ConfigJson.ToToken(new ConfigObject(Runtime(graph).Config))["variables"]!;
        Assert.Equal(["BETA", "ZED", "alpha"], list.Select(i => (string?)i["name"]));
        Assert.Equal(["b", "z", "a"], list.Select(i => (string?)i["value"]));
        Assert.Equal(new Provenance("workload.yaml", "container.variables", Layer.Workload), Runtime(graph).Provenance["variables.ZED"]);
    }

    [Fact]
    public async Task A_workload_without_variables_gets_an_empty_list()
    {
        var graph = await ResolveAsync(string.Empty);

        Assert.Empty(graph.Errors);
        Assert.Empty((JArray)ConfigJson.ToToken(new ConfigObject(Runtime(graph).Config))["variables"]!);
    }

    [Fact]
    public async Task A_static_export_resolves_at_preview_and_one_that_reads_a_node_output_is_pending_with_an_edge()
    {
        var graph = await ResolveAsync("    STATIC: ${resource.sqldb.endpoint}\n    MIXED: 'https://${resource.sqldb.endpoint}/${resource.sqldb.dyn}'\n    DYN: ${resource.sqldb.dyn}\n");

        Assert.Empty(graph.Errors);
        var runtime = Runtime(graph);
        var variables = Variables(runtime);
        Assert.Equal("h1", Value(variables.Properties["STATIC"]));
        Assert.IsType<Pending>(Assert.IsType<ConfigText>(variables.Properties["DYN"]).Result);
        Assert.IsType<Pending>(Assert.IsType<ConfigText>(variables.Properties["MIXED"]).Result);
        Assert.Equal(["shop/dev/sqldb/n"], runtime.DependsOn);
        Assert.Equal(Phase.Runtime, runtime.Phase);
        Assert.Equal(("reg/app:1", 8080), (graph.WorkloadImage, graph.WorkloadPort));
    }

    [Fact]
    public async Task A_variable_that_reads_only_static_exports_adds_no_edge()
    {
        var graph = await ResolveAsync("    STATIC: ${resource.sqldb.endpoint}\n");

        Assert.Empty(Runtime(graph).DependsOn);
    }

    [Theory]
    [InlineData("    BAD: ${resource.nope.endpoint}\n", "resource.nope.endpoint")]
    [InlineData("    BAD: ${resource.sqldb.nope}\n", "resource.sqldb.nope")]
    public async Task An_unknown_resource_id_or_export_is_an_error_naming_the_variable(string variables, string reference)
    {
        var graph = await ResolveAsync(variables);

        var error = Assert.Single(graph.Errors);
        Assert.Equal(("workload.yaml", "container.variables.BAD"), (error.File, error.Location));
        Assert.Contains(reference, error.Message);
    }

    [Fact]
    public async Task A_variable_that_fails_to_evaluate_is_reported_against_the_workload_file()
    {
        var graph = await ResolveAsync("    BAD: ${env.nope}\n");

        var error = Assert.Single(graph.Errors);
        Assert.Equal(("workload.yaml", "container.variables.BAD"), (error.File, error.Location));
        Assert.Contains("env.nope", error.Message);
    }

    [Fact]
    public async Task A_grant_node_cannot_read_an_export_but_another_workload_node_can()
    {
        const string add = "add:\n  reader:\n    template: t/reader\n    config: { v: '${resource.sqldb.endpoint}' }\n  granter:\n    kind: grant\n    template: t/grant\n    config: { v: '${resource.sqldb.endpoint}' }\n";

        var graph = await ResolveAsync(string.Empty, [WorkloadPolicy("readers", add)]);

        var error = Assert.Single(graph.Errors);
        Assert.Equal("v", error.Location);
        Assert.Contains("node 'shop/dev/@workload/granter'", error.Message);
        Assert.Contains("a grant cannot read an export", error.Message);
    }

    [Fact]
    public async Task A_requirements_node_config_still_cannot_read_another_requirement()
    {
        var graph = await ResolveAsync(string.Empty, mapping: Mapping(", peer: '${resource.sqldb.endpoint}'"));

        var error = Assert.Single(graph.Errors);
        Assert.Equal("peer", error.Location);
        Assert.Contains("node config cannot reference another requirement", error.Message);
    }

    [Fact]
    public async Task A_workload_scope_set_wins_over_a_workload_variable_and_a_default_does_not()
    {
        var policy = WorkloadPolicy("vars", "set: { runtime.variables.SET_ME: from-policy }\ndefault: { runtime.variables.KEEP: policy-default, runtime.variables.FRESH: policy-default }\n");

        var graph = await ResolveAsync("    SET_ME: from-workload\n    KEEP: from-workload\n", [policy]);

        Assert.Empty(graph.Errors);
        var runtime = Runtime(graph);
        var variables = Variables(runtime);
        Assert.True(variables.AsEntries);
        Assert.Equal(["from-policy", "from-workload", "policy-default"], [Value(variables.Properties["SET_ME"]), Value(variables.Properties["KEEP"]), Value(variables.Properties["FRESH"])]);
        Assert.Equal(new Provenance("f5.yaml", "vars", Layer.PolicySet), runtime.Provenance["variables.SET_ME"]);
        Assert.Equal(Layer.Workload, runtime.Provenance["variables.KEEP"].Layer);
        Assert.Equal(new Provenance("f5.yaml", "vars", Layer.PolicyDefault), runtime.Provenance["variables.FRESH"]);
    }

    [Fact]
    public async Task A_variable_set_by_a_policy_can_read_a_node_the_policies_add_and_waits_for_it()
    {
        var policy = WorkloadPolicy(
            "monitoring",
            "add:\n  appinsights:\n    template: t/appi\n    config: { k: 1 }\nset: { runtime.variables.CONNECTION: '${appinsights.connectionString}' }\n");

        var graph = await ResolveAsync(string.Empty, [policy]);

        Assert.Empty(graph.Errors);
        var runtime = Runtime(graph);
        Assert.IsType<Pending>(Assert.IsType<ConfigText>(Variables(runtime).Properties["CONNECTION"]).Result);
        Assert.Equal(["shop/dev/@workload/appinsights"], runtime.DependsOn);
        Assert.Equal(Phase.Infrastructure, graph.Nodes.Single(n => n.Name == "appinsights").Phase);
    }

    [Fact]
    public async Task Two_policies_setting_one_variable_differently_conflict()
    {
        var graph = await ResolveAsync(
            string.Empty,
            [WorkloadPolicy("alpha", "set: { runtime.variables.X: one }\n"), WorkloadPolicy("zeta", "set: { runtime.variables.X: two }\n")]);

        var error = Assert.Single(graph.Errors);
        Assert.Contains("'alpha'", error.Message);
        Assert.Contains("'zeta'", error.Message);
        Assert.Contains("'runtime.variables.X'", error.Message);
        Assert.Equal("set.runtime.variables.X", error.Location);
    }

    [Theory]
    [InlineData("set: { runtime.variables: x }\n", "set.runtime.variables")]
    [InlineData("default: { runtime.variables: x }\n", "default.runtime.variables")]
    public async Task A_policy_cannot_give_the_whole_variables_field_but_its_keys_are_fine(string body, string location)
    {
        var graph = await ResolveAsync("    A: a\n", [WorkloadPolicy("whole", body)]);

        var error = Assert.Single(graph.Errors);
        Assert.Equal(location, error.Location);
        Assert.Contains("give its keys", error.Message);
    }

    [Fact]
    public async Task A_policy_cannot_give_a_path_below_a_variable()
    {
        var graph = await ResolveAsync("    A: a\n", [WorkloadPolicy("deep", "set: { runtime.variables.NEW.deep: x }\n")]);

        var error = Assert.Single(graph.Errors);
        Assert.Equal("set.runtime.variables.NEW.deep", error.Location);
        Assert.Contains("a single value", error.Message);
    }

    [Fact]
    public async Task Entries_are_only_valid_in_the_runtime_mapping()
    {
        var graph = await ResolveAsync(string.Empty, mapping: Mapping(", vars: { fn::entries: workload.variables }"));

        var error = Assert.Single(graph.Errors);
        Assert.Contains("fn::entries is not valid here", error.Message);
    }

    [Theory]
    [InlineData("requirement mapping")]
    [InlineData("policy add")]
    [InlineData("policy set")]
    public async Task A_literal_map_as_the_source_of_entries_is_an_error_anywhere(string where)
    {
        const string literal = "{ fn::entries: { A: x } }";
        var graph = where switch
        {
            "requirement mapping" => await ResolveAsync(string.Empty, mapping: Mapping($", vars: {literal}")),
            "policy add" => await ResolveAsync(string.Empty, [WorkloadPolicy("pol", $"add:\n  extra:\n    template: t/x\n    config: {{ vars: {literal} }}\n")]),
            _ => await ResolveAsync(string.Empty, [WorkloadPolicy("pol", $"set: {{ runtime.label: {literal} }}\n")]),
        };

        var error = Assert.Single(graph.Errors);
        Assert.Contains("fn::entries is not valid here", error.Message);
    }

    [Fact]
    public async Task A_variable_named_like_the_form_is_an_ordinary_variable()
    {
        var graph = await ResolveAsync("    \"fn::entries\": x\n");

        Assert.Empty(graph.Errors);
        Assert.Equal("x", Value(Variables(Runtime(graph)).Properties["fn::entries"]));
    }

    [Fact]
    public async Task A_grant_node_in_the_runtime_mapping_cannot_read_a_static_export_or_the_workload_variables()
    {
        string Mapping(string config) => $"kind: Mapping\nmatch: {{ kind: runtime }}\nnodes:\n  runtime:\n    template: t/runtime\n    config: {{ k: 1 }}\n  granter:\n    kind: grant\n    template: t/grant\n    config: {{ {config} }}\n";

        var export = await ResolveAsync(string.Empty, runtimeMapping: Mapping("v: '${resource.sqldb.endpoint}'"));
        var entries = await ResolveAsync("    A: a\n", runtimeMapping: Mapping("vars: { fn::entries: workload.variables }"));

        Assert.Contains("a grant cannot read an export", Assert.Single(export.Errors).Message);
        Assert.Contains("fn::entries is not valid here", Assert.Single(entries.Errors).Message);
    }

    [Fact]
    public async Task The_form_is_part_of_the_node_hash()
    {
        var plain = RuntimeMapping.Replace("variables:\n        fn::entries: workload.variables", "variables: { A: x }", StringComparison.Ordinal);
        Assert.NotEqual(RuntimeMapping, plain);

        var entries = await ResolveAsync("    A: x\n");
        var map = await ResolveAsync("    A: x\n", runtimeMapping: plain);

        // The same name and value, but one is deployed as a list of items and the other as an object: the hash must tell them apart.
        Assert.Empty(entries.Errors);
        Assert.Empty(map.Errors);
        Assert.NotEqual(Runtime(entries).Hash, Runtime(map).Hash);
    }

    [Fact]
    public async Task An_unknown_entries_source_is_an_error()
    {
        var graph = await ResolveAsync(string.Empty, runtimeMapping: RuntimeMapping.Replace("fn::entries: workload.variables", "fn::entries: workload.nope", StringComparison.Ordinal));

        var error = Assert.Single(graph.Errors);
        Assert.Contains("fn::entries is not valid here", error.Message);
        Assert.Contains("workload.variables", error.Message);
    }

    [Fact]
    public async Task Every_runtime_node_is_evaluated_with_its_own_name_as_the_current_id()
    {
        const string mapping = """
            kind: Mapping
            match: { kind: runtime }
            nodes:
              runtime:
                template: t/runtime
                config: { image: '${workload.image}' }
              sidecar:
                template: t/sidecar
                config: { label: "${name('thing')}" }
            """;
        var policy = WorkloadPolicy("named", "set: { runtime.label: \"${name('thing')}\" }\n");

        var graph = await ResolveAsync(string.Empty, [policy], mapping);

        Assert.Empty(graph.Errors);
        var sidecar = graph.Nodes.Single(n => n.Name == "sidecar");
        Assert.Equal("shop-sidecar", Value(sidecar.Config["label"]));
        // A policy value on the runtime node is evaluated with that node's name as well.
        Assert.Equal("shop-runtime", Value(Runtime(graph).Config["label"]));
    }
}
