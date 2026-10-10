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
          rt: ${runtime.principalId}
        """;

    private static async Task<ResolvedGraph> ResolveAsync(string variables, string[]? policies = null, string runtimeMapping = RuntimeMapping, string mapping = "")
    {
        var files = new[]
            {
                "kind: Catalog\nversion: \"1\"\n",
                "kind: ResourceType\nname: sqldb\ndescription: d\nclasses: [standard]\nexports: [endpoint, dyn, rt]\n",
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

    // A runtime mapping with a second node, 'granter' or 'sidecar', whose config is the given body.
    private static string RuntimeWith(string node, string kind, string body) =>
        $"kind: Mapping\nmatch: {{ kind: runtime }}\nnodes:\n  runtime:\n    template: t/runtime\n    config: {{ k: 1 }}\n  {node}:\n    kind: {kind}\n    template: t/x\n    config: {{ {body} }}\n";

    private static string RuntimeConfig(string body) =>
        $"kind: Mapping\nmatch: {{ kind: runtime }}\nnodes:\n  runtime:\n    template: t/runtime\n    config: {{ {body} }}\n";

    [Theory]
    [InlineData("a requirement node")]
    [InlineData("a policy-added node")]
    [InlineData("a policy-added grant")]
    [InlineData("a runtime-mapping node not named runtime")]
    [InlineData("a grant in the runtime mapping")]
    public async Task Only_the_runtime_node_may_read_a_requirements_export_whether_it_is_static_or_pending(string reader)
    {
        foreach (var export in new[] { "endpoint", "dyn" })
        {
            var read = $"v: '${{resource.sqldb.{export}}}'";
            var add = $"add:\n  extra:\n    kind: {(reader.Contains("grant", StringComparison.Ordinal) ? "grant" : "create")}\n    template: t/x\n    config: {{ {read} }}\n";
            var graph = reader switch
            {
                "a requirement node" => await ResolveAsync(string.Empty, mapping: Mapping($", {read}")),
                "a policy-added node" or "a policy-added grant" => await ResolveAsync(string.Empty, [WorkloadPolicy("readers", add)]),
                "a runtime-mapping node not named runtime" => await ResolveAsync(string.Empty, runtimeMapping: RuntimeWith("sidecar", "create", read)),
                _ => await ResolveAsync(string.Empty, runtimeMapping: RuntimeWith("granter", "grant", read)),
            };

            var error = Assert.Single(graph.Errors);
            Assert.Contains("only the runtime node may read a requirement's export", error.Message);
            Assert.Equal("v", error.Location);
        }
    }

    [Fact]
    public async Task A_grant_cannot_receive_the_workload_variables()
    {
        var graph = await ResolveAsync("    A: a\n", runtimeMapping: RuntimeWith("granter", "grant", "vars: { fn::entries: workload.variables }"));

        Assert.Contains("fn::entries is not valid here", Assert.Single(graph.Errors).Message);
    }

    [Fact]
    public async Task A_mapping_export_cannot_reference_a_resource()
    {
        var files = new[]
        {
            "kind: Catalog\nversion: \"1\"\n",
            "kind: ResourceType\nname: sqldb\ndescription: d\nclasses: [standard]\nexports: [endpoint, dyn, lit, quoted, apostrophes, fine]\n",
            "kind: Mapping\nmatch: { type: sqldb }\nnodes:\n  n: { template: t/n, config: {} }\nexports:\n  endpoint: ${resource.other.x}\n  dyn: ${guid(env.host, resource.other.x)}\n  lit: \"${name('resource.x')}\"\n"
                + "  quoted: \"'${resource.other.x}'\"\n  apostrophes: \"it's ${resource.other.x} isn't\"\n  fine: \"it's ${env.host} isn't\"\n",
        };

        var loaded = await CatalogParser.ParseAsync(files.Select((content, i) => new CatalogSource($"f{i}.yaml", content)));

        Assert.Equal(
            [("f2.yaml", "exports.apostrophes"), ("f2.yaml", "exports.dyn"), ("f2.yaml", "exports.endpoint"), ("f2.yaml", "exports.quoted")],
            loaded.Errors.Select(e => (e.File, e.Location)).Order());
        Assert.All(loaded.Errors, e => Assert.Contains("an export cannot reference ${resource.<id>.<export>}", e.Message));
    }

    [Fact]
    public void The_graph_builder_refuses_an_export_that_references_a_resource_instead_of_throwing()
    {
        // The loader stops this catalog, so the policy result is built by hand.
        var export = new Pending("${resource.other.x}", new HashSet<Reference> { new(ReferenceKind.Resource, "other", "x") });
        var variable = new ConfigText(new Pending("${resource.db.endpoint}", new HashSet<Reference> { new(ReferenceKind.Resource, "db", "endpoint") }), new HashSet<string>());
        var runtime = new ResolvedNode("runtime", "t/runtime", NodeKind.Create, new Dictionary<string, ConfigValue> { ["v"] = variable }, new Dictionary<string, Provenance>());
        var policy = new PolicyResult(
            "1", "shop", "crew", null, null,
            [new ResolvedRequirement("db", "sqldb", "standard", "m.yaml", new Dictionary<string, EvalResult> { ["endpoint"] = export }, [])],
            [runtime], [], []);

        var graph = new GraphBuilder(Env).Build(policy);

        var error = Assert.Single(graph.Errors);
        Assert.Equal(("m.yaml", "exports.endpoint"), (error.File, error.Location));
    }

    [Fact]
    public async Task A_variable_cannot_read_an_export_that_waits_on_the_runtime()
    {
        var graph = await ResolveAsync("    BAD: ${resource.sqldb.rt}\n");

        var error = Assert.Single(graph.Errors);
        Assert.Equal(("workload.yaml", "container.variables.BAD"), (error.File, error.Location));
        Assert.Contains("the runtime cannot read an export that waits on the runtime", error.Message);
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

    [Theory]
    [InlineData("a requirement mapping")]
    [InlineData("a literal map source")]
    [InlineData("nested in an object")]
    [InlineData("in an array item")]
    [InlineData("on a node that is not the runtime")]
    [InlineData("a policy add")]
    [InlineData("a policy set value")]
    public async Task Entries_are_an_error_anywhere_but_a_top_level_field_of_the_runtime_node(string where)
    {
        const string form = "{ fn::entries: workload.variables }";
        var graph = where switch
        {
            "a requirement mapping" => await ResolveAsync(string.Empty, mapping: Mapping($", vars: {form}")),
            "a literal map source" => await ResolveAsync(string.Empty, runtimeMapping: RuntimeMapping.Replace("fn::entries: workload.variables", "fn::entries: { A: x }", StringComparison.Ordinal)),
            "nested in an object" => await ResolveAsync(string.Empty, runtimeMapping: RuntimeConfig($"outer: {{ inner: {form} }}")),
            "in an array item" => await ResolveAsync(string.Empty, runtimeMapping: RuntimeConfig($"items: [{form}]")),
            "on a node that is not the runtime" => await ResolveAsync(string.Empty, runtimeMapping: RuntimeWith("sidecar", "create", $"vars: {form}")),
            "a policy add" => await ResolveAsync(string.Empty, [WorkloadPolicy("pol", $"add:\n  extra:\n    template: t/x\n    config: {{ vars: {form} }}\n")]),
            _ => await ResolveAsync(string.Empty, [WorkloadPolicy("pol", $"set: {{ runtime.label: {form} }}\n")]),
        };

        Assert.Contains("fn::entries is not valid here", graph.Errors.First().Message);
    }

    [Theory]
    [InlineData("set", "{ a: 1 }")]
    [InlineData("set", "5")]
    [InlineData("set", "[a]")]
    [InlineData("set", "true")]
    [InlineData("default", "{ a: 1 }")]
    [InlineData("default", "5")]
    [InlineData("default", "~")]
    public async Task A_policy_value_under_the_variables_must_be_a_string(string layer, string value)
    {
        var graph = await ResolveAsync("    A: a\n", [WorkloadPolicy("pol", $"{layer}: {{ runtime.variables.NEW: {value} }}\n")]);

        var error = Assert.Single(graph.Errors);
        Assert.Equal($"{layer}.runtime.variables.NEW", error.Location);
        Assert.Contains("an entry is a string", error.Message);
    }

    [Fact]
    public async Task A_policy_value_under_the_variables_may_be_a_string_expression()
    {
        var graph = await ResolveAsync("    A: a\n", [WorkloadPolicy("pol", "set: { runtime.variables.NEW: \"${env.host}-x\" }\ndefault: { runtime.variables.OTHER: plain }\n")]);

        Assert.Empty(graph.Errors);
        Assert.Equal(["h1-x", "plain"], [Value(Variables(Runtime(graph)).Properties["NEW"]), Value(Variables(Runtime(graph)).Properties["OTHER"])]);
    }

    [Fact]
    public async Task A_variable_named_like_the_form_is_an_ordinary_variable()
    {
        var graph = await ResolveAsync("    \"fn::entries\": x\n");

        Assert.Empty(graph.Errors);
        Assert.Equal("x", Value(Variables(Runtime(graph)).Properties["fn::entries"]));
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
