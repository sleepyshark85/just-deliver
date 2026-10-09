using jd.core.bp;
using jd.definitionvalidator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// Environment definitions resolve through the same engine as workloads; the descriptor is composed from the (here faked) node outputs.
public class SubstrateTests
{
    private const string Region = "region-1";

    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", "environments", name);

    private static async Task<Catalog> CatalogAsync() => (await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog"))).Catalog!;

    private static async Task<EnvironmentDefinition> DefinitionAsync(string file = "shared.yaml", Func<string, string>? edit = null)
    {
        var path = Sample(file);
        if (edit is not null)
        {
            var text = edit(await File.ReadAllTextAsync(path));
            path = Path.Combine(Directory.CreateTempSubdirectory("jd-def").FullName, file);
            await File.WriteAllTextAsync(path, text);
        }

        var result = await EnvironmentDefinitionFile.LoadAsync(path);
        Assert.Empty(result.Errors);
        return result.Definition!;
    }

    private static object? Scalar(ResolvedGraph graph, string node, string field) => ((ConfigScalar)graph.Nodes.Single(n => n.Name == node).Config[field]).Value.ToObject<object>();

    private static readonly Dictionary<string, Dictionary<string, string>> SharedOutputs = new()
    {
        ["@shared/shared/substrate/group"] = new() { ["resourceGroupName"] = "rg-shared-1", ["location"] = Region },
        ["@shared/shared/substrate/workspace"] = new() { ["workspaceId"] = "/ws/id", ["workspaceName"] = "law-1", ["workspaceCustomerId"] = "cust-1" },
        ["@shared/shared/substrate/cosmos"] = new() { ["accountId"] = "/cosmos/id", ["accountName"] = "cosmos-1", ["documentEndpoint"] = "https://cosmos-1.documents.azure.com:443/" },
    };

    private static readonly Dictionary<string, Dictionary<string, string>> DevOutputs = new()
    {
        ["@dev/dev/substrate/group"] = new() { ["resourceGroupName"] = "rg-dev-1", ["location"] = Region },
        ["@dev/dev/substrate/apps"] = new() { ["environmentId"] = "/apps/id", ["defaultDomain"] = "dev.example.io" },
        ["@dev/dev/substrate/database"] = new() { ["databaseId"] = "/db/id", ["databaseName"] = "db-dev" },
    };

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigEntry>> Outputs(Dictionary<string, Dictionary<string, string>> outputs) =>
        outputs.ToDictionary(o => o.Key, o => (IReadOnlyDictionary<string, ConfigEntry>)o.Value.ToDictionary(v => v.Key, v => new ConfigEntry(v.Value)));

    private static async Task<(ResolvedGraph Graph, DescriptorComposer Composer)> PrepareAsync(EnvironmentDefinition definition, EnvironmentDescriptor? baseDescriptor = null)
    {
        var catalog = await CatalogAsync();
        var environment = definition.Over(Region, baseDescriptor);
        var graph = Resolver.ResolveSubstrate(definition, "definition.yaml", catalog, environment);
        Assert.Empty(graph.Errors);
        return (graph, new DescriptorComposer(definition, "definition.yaml", catalog, environment, graph));
    }

    private static async Task<EnvironmentDescriptor> ComposeAsync(DescriptorComposer composer, Dictionary<string, Dictionary<string, string>> outputs)
    {
        var composed = await composer.ComposeAsync(Outputs(outputs));
        Assert.Empty(composed.Errors);
        var parsed = await EnvironmentParser.ParseAsync("composed.yaml", composed.Yaml!);
        Assert.Empty(parsed.Errors);
        return parsed.Descriptor!;
    }

    [Fact]
    public async Task The_shared_definition_resolves_with_the_free_tier_settings_from_the_catalog()
    {
        var (graph, _) = await PrepareAsync(await DefinitionAsync());

        Assert.Equal(["@shared/shared/substrate/cosmos", "@shared/shared/substrate/group", "@shared/shared/substrate/workspace"], graph.Nodes.Select(n => n.Id).Order(StringComparer.Ordinal).ToList());
        Assert.Equal("@shared/shared/substrate/group", graph.Nodes[0].Id);
        Assert.Equal(true, Scalar(graph, "cosmos", "enableFreeTier"));
        Assert.Equal(1000L, Scalar(graph, "cosmos", "totalThroughputLimit"));
        Assert.Equal(0.15, Scalar(graph, "workspace", "dailyQuotaGb"));
    }

    [Fact]
    public async Task The_environment_definition_resolves_on_the_shared_descriptor_with_the_shared_database_throughput()
    {
        var (_, shared) = await PrepareAsync(await DefinitionAsync());
        var sharedDescriptor = await ComposeAsync(shared, SharedOutputs);

        var (graph, _) = await PrepareAsync(await DefinitionAsync("dev.yaml"), sharedDescriptor);

        Assert.Equal(400L, Scalar(graph, "database", "throughput"));
        var apps = graph.Nodes.Single(n => n.Name == "apps");
        Assert.Equal(new Resolved("law-1"), ((ConfigText)apps.Config["workspaceName"]).Result);
        Assert.Equal(["@dev/dev/substrate/group"], apps.DependsOn);
    }

    [Fact]
    public async Task The_composed_descriptors_chain_and_serve_a_sample_workload()
    {
        var (_, shared) = await PrepareAsync(await DefinitionAsync());
        var sharedDescriptor = await ComposeAsync(shared, SharedOutputs);
        var (_, dev) = await PrepareAsync(await DefinitionAsync("dev.yaml"), sharedDescriptor);

        var descriptor = await ComposeAsync(dev, DevOutputs);

        Assert.Equal(("dev", Region, "team"), (descriptor.Name, descriptor.Region, descriptor.Tier));
        Assert.Equal("cosmos-1", descriptor.Values["cosmos.accountName"]);
        Assert.Equal("db-dev", descriptor.Values["cosmos.databaseName"]);
        Assert.Equal("rg-dev-1", descriptor.Values["resourceGroup"]);
        Assert.Equal("rg-shared-1", descriptor.Values["shared.resourceGroup"]);
        Assert.Equal(["cosmos.accountId"], descriptor.Grantable);

        // The layout serves the existing mappings and policies: the sample workload resolves on it without errors.
        var workload = (JObject)YamlSchemaValidator.ParseYaml(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml")));
        Assert.Empty(Resolver.Resolve(workload, "workload.yaml", await CatalogAsync(), descriptor).Errors);
    }

    [Fact]
    public async Task Before_the_deploy_values_are_checked_against_the_exports_and_nothing_needs_outputs()
    {
        var (_, composer) = await PrepareAsync(await DefinitionAsync());

        var composed = await composer.ComposeAsync(null);

        Assert.Empty(composed.Errors);
        Assert.Contains("endpoint: ${resource.substrate.cosmosEndpoint}", composed.Yaml);
    }

    [Theory]
    [InlineData("${resource.substrate.cosmosEndpoint}", "${resource.substrate.nope}")]
    [InlineData("${resource.substrate.cosmosEndpoint}", "${resource.other.cosmosEndpoint}")]
    [InlineData("${resource.substrate.cosmosEndpoint}", "${runtime.url}")]
    [InlineData("${resource.substrate.cosmosEndpoint}", "${env.nope}")]
    public async Task A_value_that_cannot_be_filled_is_found_before_anything_is_created(string original, string replacement)
    {
        var (_, composer) = await PrepareAsync(await DefinitionAsync(edit: text => text.Replace(original, replacement)));

        var errors = (await composer.ComposeAsync(null)).Errors;

        Assert.Contains(errors, e => e.Location == "values.cosmos.endpoint");
    }

    [Fact]
    public async Task A_value_without_a_usable_output_is_an_error_after_the_deploy()
    {
        var (_, composer) = await PrepareAsync(await DefinitionAsync());
        var outputs = SharedOutputs.Where(o => !o.Key.EndsWith("/cosmos", StringComparison.Ordinal)).ToDictionary(o => o.Key, o => o.Value);

        var composed = await composer.ComposeAsync(Outputs(outputs));

        Assert.Null(composed.Yaml);
        Assert.Contains(composed.Errors, e => e.Location == "values.cosmos.endpoint" && e.Message.Contains("no usable deployed output"));
    }

    [Fact]
    public async Task A_secret_or_null_output_is_never_used_and_never_appears_in_the_result()
    {
        var (_, composer) = await PrepareAsync(await DefinitionAsync());
        var outputs = Outputs(SharedOutputs).ToDictionary(o => o.Key, o => (IReadOnlyDictionary<string, ConfigEntry>)new Dictionary<string, ConfigEntry>(o.Value));
        var cosmos = new Dictionary<string, ConfigEntry>(outputs["@shared/shared/substrate/cosmos"]) { ["documentEndpoint"] = new("s3cr3t-endpoint", isSecret: true) };
        outputs["@shared/shared/substrate/cosmos"] = cosmos;
        var workspace = new Dictionary<string, ConfigEntry>(outputs["@shared/shared/substrate/workspace"]) { ["workspaceName"] = new(null) };
        outputs["@shared/shared/substrate/workspace"] = workspace;

        var composed = await composer.ComposeAsync(outputs);

        Assert.Null(composed.Yaml);
        Assert.Contains(composed.Errors, e => e.Location == "values.cosmos.endpoint" && e.Message.Contains("missing, null or secret"));
        Assert.Contains(composed.Errors, e => e.Location == "values.logAnalytics.name" && e.Message.Contains("missing, null or secret"));
        Assert.DoesNotContain("s3cr3t-endpoint", string.Join('\n', composed.Errors.Select(e => e.ToString())));
    }

    [Fact]
    public async Task A_workload_named_like_the_environment_shares_no_id_stack_or_generated_name_with_its_substrate()
    {
        var files = new[]
        {
            "kind: Catalog\nversion: \"1\"\n",
            "kind: ResourceType\nname: thing\ndescription: d\nclasses: [standard]\nexports: [out]\n",
            "kind: Naming\nrules:\n  rg:\n    pattern: \"rg-{env}-{hash}\"\n    maxLength: 90\n    allowed: \"[a-z0-9-]\"\n",
            "kind: Mapping\nmatch: { type: thing }\nnodes:\n  group:\n    template: t/group\n    config:\n      groupName: ${name('rg')}\nexports:\n  out: ${group.groupName}\n",
        }.Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var catalog = (await CatalogParser.ParseAsync(files)).Catalog!;
        var definition = new EnvironmentDefinition("dev", "team", [new JObject { ["type"] = "thing", ["id"] = "substrate" }], [], []);
        var environment = definition.Over(Region, null);
        var workload = (JObject)YamlSchemaValidator.ParseYaml("metadata: { name: dev, team: crew }\nrequires:\n  - type: thing\n    id: substrate\n");

        var substrate = Resolver.ResolveSubstrate(definition, "definition.yaml", catalog, environment).Nodes.Single();
        var workloadNode = Resolver.Resolve(workload, "workload.yaml", catalog, environment).Nodes.Single();

        Assert.Equal(("@dev/dev/substrate/group", "_dev.dev.substrate.group"), (substrate.Id, substrate.Stack));
        Assert.Equal(("dev/dev/substrate/group", "dev.dev.substrate.group"), (workloadNode.Id, workloadNode.Stack));
        Assert.NotEqual(((ConfigText)substrate.Config["groupName"]).Result, ((ConfigText)workloadNode.Config["groupName"]).Result);
    }

    [Fact]
    public async Task A_key_the_base_already_has_is_a_collision()
    {
        var (_, shared) = await PrepareAsync(await DefinitionAsync());
        var sharedDescriptor = await ComposeAsync(shared, SharedOutputs);
        var clash = await DefinitionAsync("dev.yaml", text => text.Replace("databaseName:", "accountName:"));
        var (_, composer) = await PrepareAsync(clash, sharedDescriptor);

        var composed = await composer.ComposeAsync(null);

        var error = Assert.Single(composed.Errors);
        Assert.Equal("values.cosmos.accountName", error.Location);
        Assert.Contains("already a value of the base descriptor", error.Message);
    }

    [Fact]
    public async Task The_composed_descriptor_must_pass_the_descriptor_rules()
    {
        var reserved = await DefinitionAsync(edit: text => text.Replace("values:\n", "values:\n  name: x\n"));
        var missing = await DefinitionAsync(edit: text => text + "grantable: [cosmos.nope]\n");
        var (_, a) = await PrepareAsync(reserved);
        var (_, b) = await PrepareAsync(missing);

        Assert.Contains((await a.ComposeAsync(null)).Errors, e => e.Location == "values.name");
        Assert.Contains((await b.ComposeAsync(null)).Errors, e => e.Location == "grantable.0");
    }

    [Fact]
    public async Task Definition_rules_are_reported_with_the_file()
    {
        var dir = Directory.CreateTempSubdirectory("jd-def").FullName;
        var path = Path.Combine(dir, "d.yaml");
        await File.WriteAllTextAsync(path, "kind: EnvironmentDefinition\nname: xyz\ntier: team\nrequires:\n  - type: env-substrate\n  - type: env-substrate\nvalues: {}\n");
        var duplicate = await EnvironmentDefinitionFile.LoadAsync(path);
        await File.WriteAllTextAsync(path, "kind: EnvironmentDefinition\nname: xyz\ntier: team\nvalues: {}\n");
        var schema = await EnvironmentDefinitionFile.LoadAsync(path);

        Assert.Equal($"{path}: requires[1]: requirement id 'env-substrate' is used twice; give one an 'id'.", Assert.Single(duplicate.Errors).ToString());
        Assert.Contains("requires", Assert.Single(schema.Errors).Message);
        Assert.Contains("not found", Assert.Single((await EnvironmentDefinitionFile.LoadAsync(Path.Combine(dir, "none.yaml"))).Errors).Message);
    }

    [Fact]
    public async Task Workload_scope_policies_add_nothing_to_an_environment_definition()
    {
        var (graph, _) = await PrepareAsync(await DefinitionAsync());

        Assert.DoesNotContain(graph.Nodes, n => n.Scope == GraphBuilder.WorkloadScope);
    }
}
