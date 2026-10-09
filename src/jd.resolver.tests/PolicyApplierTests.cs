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
    private const string MappingFile = "f4.yaml";

    private static readonly string[] BaseFiles =
    [
        "kind: Catalog\nversion: \"7\"\n",
        "kind: ResourceType\nname: sqldb\ndescription: d\nclasses: [standard, isolated]\nexports: [endpoint]\n",
        "kind: Naming\nrules:\n  thing:\n    pattern: \"{workload}-{id}\"\n    maxLength: 40\n    allowed: \"[a-z0-9-]\"\n",
        "kind: Roles\nroles:\n  reader: 00000000-0000-0000-0000-000000000001\n",
        "kind: Mapping\nmatch: { type: sqldb }\nnodes:\n  n:\n    template: t/n\n    config: { size: 400, keep: x, nested: { a: 1, b: 2 } }\n  m:\n    template: t/m\n    config: { size: 1 }\nexports:\n  endpoint: ${n.out}\n",
    ];

    private static string Policy(string name, string match, string body) =>
        $"kind: Policy\nname: {name}\nreason: because\nmatch: {match}\n{body}";

    private static EnvironmentDescriptor Env(string tier = "team") =>
        new("dev", "region", tier, new Dictionary<string, string> { ["host"] = "h1" }, []);

    private static async Task<PolicyResult> ApplyAsync(string[] policies, string requires = "  - type: sqldb\n", string tier = "team", bool expectExpansionErrors = false)
    {
        var files = BaseFiles.Concat(policies).Append(TestCatalog.RuntimeMapping).Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var loaded = await CatalogParser.ParseAsync(files);
        Assert.Empty(loaded.Errors);
        var workload = (JObject)YamlSchemaValidator.ParseYaml($"metadata: {{ name: shop, team: crew }}\nrequires:\n{requires}");
        var expansion = new Expander(loaded.Catalog!, Env(tier)).Expand(workload, "workload.yaml");
        Assert.Equal(expectExpansionErrors, expansion.Errors.Count > 0);
        return new PolicyApplier(loaded.Catalog!, Env(tier)).Apply(expansion);
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
        Assert.Equal(new Provenance("f5.yaml", "pol", Layer.PolicySet), node.Provenance["size"]);
        Assert.Equal(new Provenance(MappingFile, MappingFile, Layer.Mapping), node.Provenance["keep"]);
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
        Assert.Equal(new Provenance(MappingFile, MappingFile, Layer.Mapping), node.Provenance["size"]);
        Assert.Equal("h1", Text(node.Config["fresh"]));
        Assert.Equal(new Provenance("f5.yaml", "a-default", Layer.PolicyDefault), node.Provenance["fresh"]);
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
        Assert.Equal(Layer.Mapping, node.Provenance["nested.a"].Layer);
    }

    [Fact]
    public async Task A_path_through_a_non_object_is_an_error()
    {
        var result = await ApplyAsync([Policy("pol", "{ template: t/n }", "set: { 'keep.inner': 1 }\n")]);

        var error = Assert.Single(result.Errors);
        Assert.Equal(("f5.yaml", "set.keep.inner"), (error.File, error.Location));
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
        Assert.Contains("'alpha' (f6.yaml)", error.Message);
        Assert.Contains("'zeta' (f5.yaml)", error.Message);
        Assert.Contains("'size'", error.Message);
        Assert.Equal("f6.yaml", error.File);
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
    public async Task Template_type_class_and_tier_keys_select_nodes()
    {
        var result = await ApplyAsync(
        [
            Policy("by-template", "{ template: t/m }", "set: { by: template }\n"),
            Policy("by-type", "{ type: sqldb, class: isolated }", "set: { by: type }\n"),
            Policy("by-tier", "{ tier: protected }", "set: { guarded: true }\n"),
            Policy("everything", "{ type: sqldb, class: standard, tier: team }", "set: { all: true }\n"),
        ], "  - type: sqldb\n  - type: sqldb\n    id: iso\n    class: isolated\n");

        Assert.Equal("template", Text(Node(result, "m").Config["by"]));
        Assert.False(Node(result, "n").Config.ContainsKey("by"));
        Assert.False(Node(result, "n").Config.ContainsKey("guarded"));
        Assert.True((bool)Assert.IsType<ConfigScalar>(Node(result, "n").Config["all"]).Value);
        Assert.False(Node(result, "n", 1).Config.ContainsKey("all"));
        Assert.Equal("type", Text(Node(result, "n", 1).Config["by"]));
    }

    [Fact]
    public async Task The_environment_tier_selects_tier_policies()
    {
        var protectedTier = await ApplyAsync([Policy("pol", "{ template: t/n, tier: protected }", "set: { failover: true }\n")], tier: "protected");
        var team = await ApplyAsync([Policy("pol", "{ template: t/n, tier: protected }", "set: { failover: true }\n")]);

        Assert.True(Node(protectedTier, "n").Config.ContainsKey("failover"));
        Assert.False(Node(team, "n").Config.ContainsKey("failover"));
    }

    private const string Monitoring =
        "add:\n  appi:\n    template: t/appi\n    config: { name: \"${name('thing')}\", host: '${env.host}' }\n  appi-access:\n    kind: grant\n    template: t/role\n    config: { scope: '${appi.id}', who: '${runtime.principalId}' }\n"
        + "set: { runtime.connection: '${appi.cs}' }\ndefault: { runtime.other: x }\n";

    [Fact]
    public async Task The_runtime_nodes_come_from_the_runtime_mapping_with_its_probe_and_get_node_scope_policies()
    {
        var result = await ApplyAsync([Policy("size", "{ template: t/runtime }", "set: { k: 2 }\n")]);

        var runtime = Assert.Single(result.RuntimeNodes);
        Assert.Equal(("runtime", new Probe("/health", 200)), (runtime.Name, runtime.Probe));
        Assert.Equal(2, Number(runtime.Config["k"]));
        Assert.Equal(new Provenance("f5.yaml", "size", Layer.PolicySet), runtime.Provenance["k"]);
        Assert.Empty(result.WorkloadNodes);
    }

    [Fact]
    public async Task A_policy_cannot_add_a_node_the_runtime_mapping_already_declares()
    {
        var runtimeMapping = "kind: Mapping\nmatch: { kind: runtime }\nnodes:\n  runtime:\n    template: t/rt\n    config: {}\n  sidecar:\n    template: t/s\n    config: {}\n";
        var policy = Policy("pol", "{ kind: runtime }", "add:\n  sidecar:\n    template: t/x\n    config: {}\n");
        var loaded = await CatalogParser.ParseAsync(BaseFiles.Append(runtimeMapping).Append(policy).Select((content, i) => new CatalogSource($"f{i}.yaml", content)));
        Assert.Empty(loaded.Errors);
        var workload = (JObject)YamlSchemaValidator.ParseYaml("metadata: { name: shop, team: crew }\ncontainer: { image: i }\nrequires:\n  - type: sqldb\n");

        var result = new PolicyApplier(loaded.Catalog!, Env()).Apply(new Expander(loaded.Catalog!, Env()).Expand(workload, "workload.yaml"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("f6.yaml", "add.sidecar"), (error.File, error.Location));
        Assert.Contains("already declares", error.Message);
        Assert.Empty(result.WorkloadNodes);
    }

    [Fact]
    public async Task A_runtime_policy_adds_nodes_once_per_workload_and_they_get_node_scope_policies()
    {
        var result = await ApplyAsync(
        [
            Policy("monitoring", "{ kind: runtime }", Monitoring),
            Policy("hardening", "{ template: t/appi }", "set: { retention: 30 }\n"),
        ], "  - type: sqldb\n    id: one\n  - type: sqldb\n    id: two\n");

        Assert.Empty(result.Errors);
        Assert.Equal(["appi", "appi-access"], result.WorkloadNodes.Select(n => n.Name));
        Assert.All(result.Requirements, r => Assert.DoesNotContain(r.Nodes, n => n.Name.StartsWith("appi", StringComparison.Ordinal)));
        var appi = result.WorkloadNodes[0];
        Assert.Equal("shop-appi", Text(appi.Config["name"]));
        Assert.Equal(30, Number(appi.Config["retention"]));
        Assert.Equal(new Provenance("f5.yaml", "monitoring", Layer.PolicyAdd), appi.Provenance["name"]);
        Assert.Equal(new Provenance("f6.yaml", "hardening", Layer.PolicySet), appi.Provenance["retention"]);
        var access = result.WorkloadNodes[1];
        Assert.Equal(NodeKind.Grant, access.Kind);
        Assert.Equal(Layer.PolicyAdd, access.Provenance["scope"].Layer);
        Assert.IsType<Pending>(Assert.IsType<ConfigText>(access.Config["who"]).Result);
    }

    [Fact]
    public async Task Runtime_targeted_set_and_default_entries_are_not_applied_or_stored()
    {
        var result = await ApplyAsync([Policy("monitoring", "{ kind: runtime }", Monitoring)]);

        Assert.Empty(result.Errors);
        Assert.DoesNotContain(result.Requirements.SelectMany(r => r.Nodes).Concat(result.WorkloadNodes), n => n.Config.ContainsKey("runtime"));
        Assert.DoesNotContain(result.WorkloadNodes.Concat(result.Requirements.SelectMany(r => r.Nodes)), n => n.Provenance.Keys.Any(k => k.StartsWith("runtime", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_runtime_policy_never_applies_to_node_config_and_a_tier_narrows_workload_scope()
    {
        var policies = new[]
        {
            Policy("runtime-set", "{ kind: runtime }", "set: { runtime.size: 1 }\n"),
            Policy("guarded-add", "{ kind: runtime, tier: protected }", "add:\n  extra:\n    template: t/x\n    config: {}\n"),
        };

        var team = await ApplyAsync(policies);
        var guarded = await ApplyAsync(policies, tier: "protected");

        Assert.Equal(400, Number(Node(team, "n").Config["size"]));
        Assert.All(team.Requirements.SelectMany(r => r.Nodes), n => Assert.False(n.Config.ContainsKey("runtime")));
        Assert.Empty(team.WorkloadNodes);
        Assert.Equal("extra", Assert.Single(guarded.WorkloadNodes).Name);
    }

    [Fact]
    public async Task Expansion_errors_carry_into_the_policy_result()
    {
        var result = await ApplyAsync([], "  - type: sqldb\n  - type: unmapped\n", expectExpansionErrors: true);

        var error = Assert.Single(result.Errors);
        Assert.Equal(("workload.yaml", "requires[1]"), (error.File, error.Location));
        Assert.Single(result.Requirements);
    }

    [Fact]
    public async Task Two_policies_adding_a_node_with_the_same_name_is_an_error_naming_both_files()
    {
        var add = "add:\n  extra:\n    template: t/x\n    config: {}\n";

        var result = await ApplyAsync([Policy("first", "{ kind: runtime }", add), Policy("second", "{ kind: runtime }", add)]);

        var error = Assert.Single(result.Errors);
        Assert.Contains("'first' (f5.yaml)", error.Message);
        Assert.Contains("'second' (f6.yaml)", error.Message);
        Assert.Contains("'extra'", error.Message);
        Assert.Single(result.WorkloadNodes);
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
        Assert.Contains("'inner' (f6.yaml)", error.Message);
        Assert.Contains("'outer' (f5.yaml)", error.Message);
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
        Assert.Equal(("f5.yaml", "set.a"), (error.File, error.Location));
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
            Policy("monitoring", "{ kind: runtime }", Monitoring),
            Policy("pol", "{ template: t/n }", "set: { size: 1, 'x.y': 2 }\ndefault: { fresh: 1 }\n"),
        ]);

        static IEnumerable<string> Leaves(string prefix, ConfigValue value) => value is ConfigObject o
            ? o.Properties.SelectMany(p => Leaves(prefix.Length == 0 ? p.Key : $"{prefix}.{p.Key}", p.Value))
            : [prefix];

        var nodes = result.Requirements.SelectMany(r => r.Nodes).Concat(result.WorkloadNodes).ToList();
        Assert.NotEmpty(nodes);
        Assert.All(nodes, n => Assert.Equal(
            Leaves(string.Empty, new ConfigObject(n.Config)).Order(StringComparer.Ordinal),
            n.Provenance.Keys.Order(StringComparer.Ordinal)));
        Assert.Equal(
            [Layer.Mapping, Layer.PolicySet, Layer.PolicyDefault, Layer.PolicyAdd],
            nodes.SelectMany(n => n.Provenance.Values).Select(v => v.Layer).Distinct().Order());
    }

    [Fact]
    public async Task Seed_catalog_and_sample_workload_gain_the_monitoring_nodes_without_errors()
    {
        var catalog = (await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog"))).Catalog!;
        var workload = (JObject)YamlSchemaValidator.ParseYaml(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml")));
        var environment = new EnvironmentDescriptor(
            "dev",
            "region",
            "team",
            new Dictionary<string, string>
            {
                ["resourceGroup"] = "rg",
                ["containerAppsEnvironment.id"] = "/cae",
                ["cosmos.accountName"] = "acct",
                ["cosmos.databaseName"] = "db",
                ["cosmos.accountId"] = "/acct",
                ["cosmos.endpoint"] = "https://acct",
                ["logAnalytics.id"] = "/law",
            },
            []);
        var expansion = new Expander(catalog, environment).Expand(workload, "workload.yaml");
        Assert.Empty(expansion.Errors);

        var result = new PolicyApplier(catalog, environment).Apply(expansion);

        Assert.Empty(result.Errors);
        Assert.Equal(["appinsights", "appinsights-access"], result.WorkloadNodes.Select(n => n.Name));
        Assert.Equal(catalog.Version, result.CatalogVersion);
        Assert.Equal(new Provenance("policies/enforce-monitoring.yaml", "enforce-monitoring", Layer.PolicyAdd), result.WorkloadNodes[0].Provenance["workspaceResourceId"]);
    }
}
