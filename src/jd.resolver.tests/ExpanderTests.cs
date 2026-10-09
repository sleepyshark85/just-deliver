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
        "kind: Catalog\nversion: \"1\"\n",
        "kind: ResourceType\nname: sqldb\ndescription: d\nclasses: [standard, isolated]\nexports: [endpoint]\n",
        "kind: Naming\nrules:\n  thing:\n    pattern: \"{workload}-{id}\"\n    maxLength: 40\n    allowed: \"[a-z0-9-]\"\n",
        "kind: Roles\nroles:\n  reader: 00000000-0000-0000-0000-000000000001\n",
    ];

    private static string Mapping(string match, string nodes = "  n:\n    template: t/n\n    config: { a: 1 }\n", string exports = "  endpoint: ${n.out}\n") =>
        $"kind: Mapping\nmatch: {match}\nnodes:\n{nodes}exports:\n{exports}";

    private static EnvironmentDescriptor Env(string tier = "team") =>
        new("dev", "region", tier, new Dictionary<string, string> { ["host"] = "h1" }, []);

    private static async Task<Catalog> LoadAsync(params string[] mappings)
    {
        var files = BaseFiles.Concat(mappings).Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var result = await CatalogParser.ParseAsync(files);
        Assert.Empty(result.Errors);
        return result.Catalog!;
    }

    private static JObject Workload(string requires) =>
        (JObject)YamlSchemaValidator.ParseYaml($"metadata: {{ name: shop, team: crew }}\nrequires:\n{requires}");

    private static ExpansionResult Expand(Catalog catalog, string requires, string tier = "team") =>
        new Expander(catalog, Env(tier)).Expand(Workload(requires), "workload.yaml");

    [Fact]
    public async Task Single_match_expands_nodes_and_exports_with_the_requirement_id()
    {
        var catalog = await LoadAsync(Mapping(
            "{ type: sqldb, class: standard }",
            "  n:\n    template: t/n\n    kind: grant\n    config:\n      name: ${name('thing')}\n      host: ${env.host}\n",
            "  endpoint: ${env.host}\n"));

        var result = Expand(catalog, "  - type: sqldb\n    id: orders\n");

        Assert.Empty(result.Errors);
        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(("orders", "sqldb", "standard", "f4.yaml"), (requirement.Id, requirement.Type, requirement.Class, requirement.Mapping));
        var node = Assert.Single(requirement.Nodes);
        Assert.Equal(("n", "t/n", NodeKind.Grant), (node.Name, node.Template, node.Kind));
        Assert.Equal("shop-orders", Text(node.Config["name"]));
        Assert.Equal("h1", Text(node.Config["host"]));
        Assert.Equal("h1", Assert.IsType<Resolved>(requirement.Exports["endpoint"]).Value);
    }

    [Fact]
    public async Task The_most_specific_mapping_wins()
    {
        var catalog = await LoadAsync(Mapping("{ type: sqldb }", exports: "  endpoint: general\n"), Mapping("{ type: sqldb, class: isolated }", exports: "  endpoint: specific\n"));

        var isolated = Assert.Single(Expand(catalog, "  - type: sqldb\n    class: isolated\n").Requirements);
        var standard = Assert.Single(Expand(catalog, "  - type: sqldb\n").Requirements);

        Assert.Equal("specific", Assert.IsType<Resolved>(isolated.Exports["endpoint"]).Value);
        Assert.Equal("general", Assert.IsType<Resolved>(standard.Exports["endpoint"]).Value);
    }

    [Fact]
    public async Task A_tie_at_the_top_is_an_error_naming_both_files()
    {
        var catalog = await LoadAsync(Mapping("{ type: sqldb, class: standard }"), Mapping("{ type: sqldb, tier: team }"));

        var result = Expand(catalog, "  - type: sqldb\n");

        var error = Assert.Single(result.Errors);
        Assert.Equal(("workload.yaml", "requires[0]"), (error.File, error.Location));
        Assert.Contains("f4.yaml, f5.yaml", error.Message);
        Assert.Empty(result.Requirements);
    }

    [Fact]
    public async Task No_match_is_an_error_naming_the_requirement_type_class_and_tier()
    {
        var catalog = await LoadAsync(Mapping("{ type: sqldb, class: standard }"));

        var result = Expand(catalog, "  - type: sqldb\n    id: orders\n    class: isolated\n");

        var error = Assert.Single(result.Errors);
        Assert.Equal("requires[0]", error.Location);
        Assert.Contains("'orders'", error.Message);
        Assert.Contains("type 'sqldb', class 'isolated', tier 'team'", error.Message);
    }

    [Fact]
    public async Task The_environment_tier_takes_part_in_matching()
    {
        var catalog = await LoadAsync(Mapping("{ type: sqldb }", exports: "  endpoint: plain\n"), Mapping("{ type: sqldb, tier: protected }", exports: "  endpoint: guarded\n"));

        var team = Assert.Single(Expand(catalog, "  - type: sqldb\n", "team").Requirements);
        var guarded = Assert.Single(Expand(catalog, "  - type: sqldb\n", "protected").Requirements);

        Assert.Equal("plain", Assert.IsType<Resolved>(team.Exports["endpoint"]).Value);
        Assert.Equal("guarded", Assert.IsType<Resolved>(guarded.Exports["endpoint"]).Value);
    }

    [Theory]
    [InlineData("{ type: sqldb, kind: runtime }")]
    [InlineData("{ type: sqldb, runtime: container-app }")]
    public async Task A_mapping_using_a_key_the_requirement_lacks_never_matches(string match)
    {
        var catalog = await LoadAsync(Mapping(match));

        var error = Assert.Single(Expand(catalog, "  - type: sqldb\n").Errors);

        Assert.Contains("no mapping matches", error.Message);
    }

    [Fact]
    public async Task Config_is_walked_through_objects_and_arrays_and_scalars_pass_through()
    {
        var catalog = await LoadAsync(Mapping(
            "{ type: sqldb }",
            "  n:\n    template: t/n\n    config:\n      size: 400\n      on: true\n      none: null\n      tags: [x, '${env.host}', 7]\n      nested: { deep: { host: '${env.host}' }, count: 2 }\n"));

        var node = Assert.Single(Assert.Single(Expand(catalog, "  - type: sqldb\n").Requirements).Nodes);

        Assert.Equal(400, (int)Assert.IsType<ConfigScalar>(node.Config["size"]).Value);
        Assert.True((bool)Assert.IsType<ConfigScalar>(node.Config["on"]).Value);
        Assert.Equal(JTokenType.Null, Assert.IsType<ConfigScalar>(node.Config["none"]).Value.Type);
        var tags = Assert.IsType<ConfigArray>(node.Config["tags"]).Items;
        Assert.Equal(("x", "h1"), (Text(tags[0]), Text(tags[1])));
        Assert.Equal(7, (int)Assert.IsType<ConfigScalar>(tags[2]).Value);
        var nested = Assert.IsType<ConfigObject>(node.Config["nested"]).Properties;
        Assert.Equal("h1", Text(Assert.IsType<ConfigObject>(nested["deep"]).Properties["host"]));
        Assert.Equal(2, (int)Assert.IsType<ConfigScalar>(nested["count"]).Value);
    }

    [Fact]
    public async Task Pending_values_carry_their_references()
    {
        var catalog = await LoadAsync(Mapping(
            "{ type: sqldb }",
            "  n:\n    template: t/n\n    config: { a: 1 }\n  m:\n    template: t/m\n    config:\n      db: ${n.databaseName}\n      who: [{ id: '${runtime.principalId}' }]\n",
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
        var catalog = await LoadAsync(Mapping(
            "{ type: sqldb }",
            "  n:\n    template: t/n\n    config: { a: '${env.nope}', b: '${role.nope}' }\n"));

        var result = Expand(catalog, "  - type: sqldb\n    id: one\n  - type: sqldb\n    id: two\n");

        Assert.Equal(4, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Equal("f4.yaml", e.File));
        Assert.Contains(result.Errors, e => e.Location == "nodes.n.config.b" && e.Message.StartsWith("for requires[1] ('two')"));
        Assert.Empty(result.Requirements);
    }

    [Fact]
    public async Task The_loaded_catalog_is_not_changed_by_expansion()
    {
        var catalog = await LoadAsync(Mapping("{ type: sqldb }", "  n:\n    template: t/n\n    config: { size: 400, tags: [1], nested: { k: v } }\n"));
        string Snapshot() => JsonConvert.SerializeObject(catalog.Mappings);
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
            "team",
            new Dictionary<string, string>
            {
                ["resourceGroup"] = "rg",
                ["cosmos.accountName"] = "acct",
                ["cosmos.accountId"] = "/acct",
                ["cosmos.endpoint"] = "https://acct",
            },
            []);

        var result = new Expander(catalog, environment).Expand(workload, "workload.yaml");

        Assert.Empty(result.Errors);
        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(["access", "container", "database"], requirement.Nodes.Select(n => n.Name).Order(StringComparer.Ordinal));
        var access = requirement.Nodes.Single(n => n.Name == "access");
        Assert.Equal(NodeKind.Grant, access.Kind);
        Assert.Equal(["container", "database", "endpoint"], requirement.Exports.Keys.Order(StringComparer.Ordinal));
        Assert.IsType<Pending>(Assert.IsType<ConfigText>(access.Config["principalId"]).Result);
    }

    private static string Text(ConfigValue value) => Assert.IsType<Resolved>(Assert.IsType<ConfigText>(value).Result).Value;
}
