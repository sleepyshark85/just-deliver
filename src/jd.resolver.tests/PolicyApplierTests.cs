using jd.definitionvalidator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.policies;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

public class PolicyApplierTests
{
    private const string ComponentFile = "f3.yaml";

    private static readonly string[] BaseFiles =
    [
        "kind: Catalog\nversion: \"7\"\nenvironmentTypes: { dev: { layer: environment }, stage: { layer: environment, approval: true } }\n",
        "kind: Naming\nrules:\n  thing:\n    pattern: \"{workload}-{id}\"\n    maxLength: 40\n    allowed: \"[a-z0-9-]\"\n",
        "kind: Roles\nroles:\n  reader: 00000000-0000-0000-0000-000000000001\n",
        "kind: Component\nname: sqldb\ndescription: d\nexports: [endpoint]\nimplementations:\n  default:\n    classes:\n      standard: &part\n        nodes:\n          n:\n            template: t/n\n            config: { size: 400, keep: x, nested: { a: 1, b: 2 } }\n          m:\n            template: t/m\n            config: { size: 1 }\n        exports:\n          endpoint: ${n.out}\n      isolated: *part\n",
    ];

    private static string Policy(string name, string match, string body) =>
        $"kind: Policy\nname: {name}\nreason: because\nmatch: {match}\n{body}";

    private static EnvironmentDescriptor Env(string type = "dev") =>
        new("dev", "region", type, new Dictionary<string, string> { ["host"] = "h1" }, [], null);

    private static async Task<PolicyResult> ApplyAsync(string[] policies, string requires = "  - type: sqldb\n", string type = "dev", bool expectExpansionErrors = false)
    {
        var files = BaseFiles.Concat(policies).Append(TestCatalog.RuntimeFile).Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var loaded = await CatalogParser.ParseAsync(files);
        Assert.Empty(loaded.Errors);
        var workload = (JObject)YamlSchemaValidator.ParseYaml($"metadata: {{ name: shop, team: crew }}\nrequires:\n{requires}");
        var expansion = new Expander(loaded.Catalog!, Env(type)).Expand(workload, "workload.yaml");
        Assert.Equal(expectExpansionErrors, expansion.Errors.Count > 0);
        return new PolicyApplier(loaded.Catalog!, Env(type)).Apply(expansion);
    }

    private static ResolvedNode Node(PolicyResult result, string name, int requirement = 0) =>
        result.Requirements[requirement].Nodes.Single(n => n.Name == name);

    private static string Text(ConfigValue value) => Assert.IsType<Resolved>(Assert.IsType<ConfigText>(value).Result).Value;

    private static ConfigValue At(ResolvedNode node, string path) =>
        path.Split('.').Aggregate((ConfigValue)new ConfigObject(node.Config), (current, segment) => ((ConfigObject)current).Properties[segment]);

    private static int Number(ConfigValue value) => (int)Assert.IsType<ConfigScalar>(value).Value;

    [Fact]
    public async Task Set_overrides_a_mapping_value_and_only_that_field()
    {
        var result = await ApplyAsync([Policy("pol", "{ template: t/n }", "set: { size: 1000 }\n")]);

        var node = Node(result, "n");
        Assert.Equal(1000, Number(node.Config["size"]));
        Assert.Equal(new Provenance("f4.yaml", "pol", Layer.PolicySet), node.Provenance["size"]);
        Assert.Equal(new Provenance(ComponentFile, ComponentFile, Layer.Implementation), node.Provenance["keep"]);
        Assert.Equal(1, Number(Node(result, "m").Config["size"]));
    }

    [Fact]
    public async Task Default_fills_absent_fields_only_and_set_beats_default()
    {
        var result = await ApplyAsync(
        [
            Policy("a-default", "{ template: t/n }", "default: { size: 5, fresh: '${env.host}', won: lost }\n"),
            Policy("b-set", "{ template: t/n }", "set: { won: strict }\n"),
        ]);

        var node = Node(result, "n");
        Assert.Equal(400, Number(node.Config["size"]));
        Assert.Equal(new Provenance(ComponentFile, ComponentFile, Layer.Implementation), node.Provenance["size"]);
        Assert.Equal("h1", Text(node.Config["fresh"]));
        Assert.Equal(new Provenance("f4.yaml", "a-default", Layer.PolicyDefault), node.Provenance["fresh"]);
        Assert.Equal("strict", Text(node.Config["won"]));
        Assert.Equal(Layer.PolicySet, node.Provenance["won"].Layer);
    }

    [Fact]
    public async Task A_dotted_path_creates_missing_objects_and_keeps_siblings()
    {
        var result = await ApplyAsync([Policy("pol", "{ template: t/n }", "set: { 'deep.er.level': strong, 'nested.b': 9 }\n")]);

        var node = Node(result, "n");
        Assert.Equal("strong", Text(At(node, "deep.er.level")));
        Assert.Equal(9, Number(At(node, "nested.b")));
        Assert.Equal(1, Number(At(node, "nested.a")));
        Assert.Equal(Layer.PolicySet, node.Provenance["deep.er.level"].Layer);
        Assert.Equal(Layer.PolicySet, node.Provenance["nested.b"].Layer);
        Assert.Equal(Layer.Implementation, node.Provenance["nested.a"].Layer);
    }

    [Fact]
    public async Task A_path_through_a_non_object_is_an_error()
    {
        var result = await ApplyAsync([Policy("pol", "{ template: t/n }", "set: { 'keep.inner': 1 }\n")]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(("f4.yaml", "set.keep.inner"), (error.File, error.Location));
        Assert.Contains("not an object", error.Message);
    }

    [Fact]
    public async Task Two_policies_setting_a_field_differently_is_an_error_naming_both_files()
    {
        var result = await ApplyAsync(
        [
            Policy("zeta", "{ template: t/n }", "set: { size: 2 }\n"),
            Policy("alpha", "{ template: t/n }", "set: { size: 3 }\n"),
        ]);

        var error = Assert.Single(result.Errors);
        Assert.Contains("'alpha' (f5.yaml)", error.Message);
        Assert.Contains("'zeta' (f4.yaml)", error.Message);
        Assert.Contains("'size'", error.Message);
        Assert.Equal("f5.yaml", error.File);
    }

    [Fact]
    public async Task Two_defaults_that_differ_are_an_error_but_the_same_value_is_not()
    {
        var differ = await ApplyAsync(
        [
            Policy("aa", "{ template: t/n }", "default: { fresh: 1 }\n"),
            Policy("bb", "{ template: t/n }", "default: { fresh: 2 }\n"),
        ]);
        var same = await ApplyAsync(
        [
            Policy("bb", "{ template: t/n }", "default: { fresh: 1 }\n"),
            Policy("aa", "{ template: t/n }", "default: { fresh: 1 }\n"),
        ]);

        Assert.Contains("default values", Assert.Single(differ.Errors).Message);
        Assert.Empty(same.Errors);
        Assert.Equal("aa", Node(same, "n").Provenance["fresh"].Rule);
    }

    [Fact]
    public async Task Policies_setting_the_same_value_are_fine_and_provenance_names_the_first_by_name()
    {
        var result = await ApplyAsync(
        [
            Policy("zeta", "{ template: t/n }", "set: { size: 2 }\n"),
            Policy("alpha", "{ template: t/n }", "set: { size: 2 }\n"),
        ]);

        Assert.Empty(result.Errors);
        Assert.Equal("alpha", Node(result, "n").Provenance["size"].Rule);
    }

    [Fact]
    public async Task Template_type_class_and_environmentType_keys_select_nodes()
    {
        var result = await ApplyAsync(
        [
            Policy("by-template", "{ template: t/m }", "set: { by: template }\n"),
            Policy("by-type", "{ type: sqldb, class: isolated }", "set: { by: type }\n"),
            Policy("by-environment-type", "{ environmentType: stage }", "set: { guarded: true }\n"),
            Policy("everything", "{ type: sqldb, class: standard, environmentType: dev }", "set: { all: true }\n"),
        ], "  - type: sqldb\n  - type: sqldb\n    id: iso\n    class: isolated\n");

        Assert.Equal("template", Text(Node(result, "m").Config["by"]));
        Assert.False(Node(result, "n").Config.ContainsKey("by"));
        Assert.False(Node(result, "n").Config.ContainsKey("guarded"));
        Assert.True((bool)Assert.IsType<ConfigScalar>(Node(result, "n").Config["all"]).Value);
        Assert.False(Node(result, "n", 1).Config.ContainsKey("all"));
        Assert.Equal("type", Text(Node(result, "n", 1).Config["by"]));
    }

    [Fact]
    public async Task The_environment_type_selects_environmentType_policies()
    {
        var gated = await ApplyAsync([Policy("pol", "{ template: t/n, environmentType: stage }", "set: { failover: true }\n")], type: "stage");
        var team = await ApplyAsync([Policy("pol", "{ template: t/n, environmentType: stage }", "set: { failover: true }\n")]);

        Assert.True(Node(gated, "n").Config.ContainsKey("failover"));
        Assert.False(Node(team, "n").Config.ContainsKey("failover"));
    }

    private const string RuntimeFields = "set: { runtime.connection: '${env.host}' }\ndefault: { runtime.other: x }\n";

    [Fact]
    public async Task The_runtime_nodes_come_from_the_runtime_mapping_with_its_probe_and_get_node_scope_policies()
    {
        var result = await ApplyAsync([Policy("size", "{ template: t/runtime }", "set: { k: 2 }\n")]);

        var runtime = Assert.Single(result.RuntimeNodes);
        Assert.Equal(("runtime", TestCatalog.Probe, TestCatalog.Release), (runtime.Name, runtime.Probe, runtime.Release));
        Assert.Equal(2, Number(runtime.Config["k"]));
        Assert.Equal(new Provenance("f4.yaml", "size", Layer.PolicySet), runtime.Provenance["k"]);
    }

    // A policy requires, it never adds (docs/architecture/components.md section 3): a workload the policy matches must declare the component.
    [Fact]
    public async Task A_workload_without_a_required_component_is_an_error_naming_the_policy_and_its_file()
    {
        var result = await ApplyAsync([Policy("needs-sqldb", "{ kind: runtime }", "require: sqldb\n")], "  []\n");

        var error = Assert.Single(result.Errors);
        Assert.Equal(("workload.yaml", "requires"), (error.File, error.Location));
        Assert.Equal("policy 'needs-sqldb' (f4.yaml) requires a 'sqldb' requirement (because); add '- type: sqldb'.", error.Message);
        Assert.Single(result.RuntimeNodes);
    }

    [Theory]
    [InlineData("  - type: sqldb\n")]
    [InlineData("  - type: sqldb\n    id: other\n    class: isolated\n")]
    public async Task A_requirement_of_the_required_type_with_any_id_and_class_satisfies_the_policy(string requires)
    {
        var result = await ApplyAsync([Policy("needs-sqldb", "{ kind: runtime }", "require: sqldb\n")], requires);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task A_require_narrowed_by_environment_type_applies_only_there()
    {
        var policy = Policy("needs-sqldb", "{ kind: runtime, environmentType: stage }", "require: sqldb\n");

        var team = await ApplyAsync([policy], "  []\n");
        var gated = await ApplyAsync([policy], "  []\n", type: "stage");

        Assert.Empty(team.Errors);
        Assert.Equal("requires", Assert.Single(gated.Errors).Location);
    }

    [Fact]
    public async Task A_declared_requirement_that_failed_to_expand_still_satisfies_the_policy()
    {
        var result = await ApplyAsync([Policy("needs-sqldb", "{ kind: runtime }", "require: sqldb\n")], "  - type: sqldb\n    class: premium\n", expectExpansionErrors: true);

        var error = Assert.Single(result.Errors);
        Assert.Equal("requires[0]", error.Location);
        Assert.Contains("class 'premium'", error.Message);
    }

    [Fact]
    public async Task Runtime_targeted_set_and_default_entries_are_not_applied_or_stored()
    {
        var result = await ApplyAsync([Policy("runtime-fields", "{ kind: runtime }", RuntimeFields)]);

        Assert.Empty(result.Errors);
        Assert.DoesNotContain(result.Requirements.SelectMany(r => r.Nodes).Concat(result.RuntimeNodes), n => n.Config.ContainsKey("runtime"));
        Assert.DoesNotContain(result.Requirements.SelectMany(r => r.Nodes).Concat(result.RuntimeNodes), n => n.Provenance.Keys.Any(k => k.StartsWith("runtime", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_runtime_policy_never_applies_to_node_config_and_an_environment_type_narrows_workload_scope()
    {
        var policies = new[]
        {
            Policy("runtime-set", "{ kind: runtime }", "set: { runtime.size: 1 }\n"),
            Policy("guarded-set", "{ kind: runtime, environmentType: stage }", "set: { runtime.guarded: 1 }\n"),
        };

        var team = await ApplyAsync(policies);
        var guarded = await ApplyAsync(policies, type: "stage");

        Assert.Equal(400, Number(Node(team, "n").Config["size"]));
        Assert.All(team.Requirements.SelectMany(r => r.Nodes), n => Assert.False(n.Config.ContainsKey("runtime")));
        Assert.False(Assert.Single(team.RuntimeNodes).Config.ContainsKey("guarded"));
        Assert.Equal(1, Number(Assert.Single(guarded.RuntimeNodes).Config["guarded"]));
    }

    [Fact]
    public async Task Expansion_errors_carry_into_the_policy_result()
    {
        var result = await ApplyAsync([], "  - type: sqldb\n  - type: unmapped\n", expectExpansionErrors: true);

        var error = Assert.Single(result.Errors);
        Assert.Equal(("workload.yaml", "requires[1]"), (error.File, error.Location));
        Assert.Single(result.Requirements);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("default")]
    public async Task A_path_that_is_a_prefix_of_another_policys_path_is_a_conflict(string layer)
    {
        var result = await ApplyAsync(
        [
            Policy("outer", "{ template: t/n }", $"{layer}: {{ fresh: {{ a: 1 }} }}\n"),
            Policy("inner", "{ template: t/n }", $"{layer}: {{ fresh.b: 2 }}\n"),
        ]);

        var error = Assert.Single(result.Errors);
        Assert.Contains("'inner' (f5.yaml)", error.Message);
        Assert.Contains("'outer' (f4.yaml)", error.Message);
        Assert.Contains("'fresh' and 'fresh.b'", error.Message);
        Assert.False(Node(result, "n").Config.ContainsKey("fresh"));
    }

    [Fact]
    public async Task Setting_an_object_replaces_its_subtree_and_its_provenance()
    {
        var result = await ApplyAsync([Policy("pol", "{ template: t/n }", "set: { nested: { c: 3 } }\n")]);

        var node = Node(result, "n");
        Assert.Equal(["nested.c"], node.Provenance.Keys.Where(k => k.StartsWith("nested", StringComparison.Ordinal)));
        Assert.Equal(Layer.PolicySet, node.Provenance["nested.c"].Layer);
    }

    [Fact]
    public async Task Evaluation_errors_name_the_policy_file_and_location()
    {
        var result = await ApplyAsync([Policy("pol", "{ template: t/n }", "set: { a: '${env.nope}' }\n")]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(("f4.yaml", "set.a"), (error.File, error.Location));
    }

    [Fact]
    public async Task The_catalog_version_is_on_the_result_and_the_catalog_is_not_changed()
    {
        var files = BaseFiles.Concat([Policy("pol", "{ template: t/n }", "set: { nested.deep: 1 }\ndefault: { list: [1] }\n")])
            .Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var catalog = (await CatalogParser.ParseAsync(files)).Catalog!;
        var before = JsonConvert.SerializeObject(catalog);
        var workload = (JObject)YamlSchemaValidator.ParseYaml("metadata: { name: shop, team: crew }\nrequires:\n  - type: sqldb\n");
        var expansion = new Expander(catalog, Env()).Expand(workload, "workload.yaml");

        var result = new PolicyApplier(catalog, Env()).Apply(expansion);

        Assert.Equal("7", result.CatalogVersion);
        ((JValue)Assert.IsType<ConfigScalar>(At(Node(result, "n"), "nested.a")).Value).Value = 999;
        ((JValue)Assert.IsType<ConfigScalar>(Assert.Single(Assert.IsType<ConfigArray>(At(Node(result, "n"), "list")).Items)).Value).Value = 999;
        Assert.Equal(before, JsonConvert.SerializeObject(catalog));
    }

    [Fact]
    public async Task Every_leaf_field_of_every_node_has_provenance()
    {
        var result = await ApplyAsync(
        [
            Policy("pol", "{ template: t/n }", "set: { size: 1, 'x.y': 2 }\ndefault: { fresh: 1 }\n"),
        ]);

        static IEnumerable<string> Leaves(string prefix, ConfigValue value) => value is ConfigObject o
            ? o.Properties.SelectMany(p => Leaves(prefix.Length == 0 ? p.Key : $"{prefix}.{p.Key}", p.Value))
            : [prefix];

        var nodes = result.Requirements.SelectMany(r => r.Nodes).ToList();
        Assert.NotEmpty(nodes);
        Assert.All(nodes, n => Assert.Equal(
            Leaves(string.Empty, new ConfigObject(n.Config)).Order(StringComparer.Ordinal),
            n.Provenance.Keys.Order(StringComparer.Ordinal)));
        Assert.Equal(
            [Layer.Implementation, Layer.PolicySet, Layer.PolicyDefault],
            nodes.SelectMany(n => n.Provenance.Values).Select(v => v.Layer).Distinct().Order());
    }

    [Fact]
    public async Task Seed_catalog_and_sample_workload_resolve_with_monitoring_as_a_requirement()
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
            [],
            null);
        var expansion = new Expander(catalog, environment).Expand(workload, "workload.yaml");
        Assert.Empty(expansion.Errors);

        var result = new PolicyApplier(catalog, environment).Apply(expansion);

        Assert.Empty(result.Errors);
        var monitoring = Assert.Single(result.Requirements, r => r.Type == "monitoring");
        Assert.Equal(["appinsights", "access"], monitoring.Nodes.Select(n => n.Name));
        Assert.Equal(catalog.Version, result.CatalogVersion);
        Assert.Equal(new Provenance("components/monitoring.yaml", "components/monitoring.yaml", Layer.Implementation), monitoring.Nodes[0].Provenance["workspaceResourceId"]);
    }
}
