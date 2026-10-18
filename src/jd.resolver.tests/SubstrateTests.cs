using System.Runtime.CompilerServices;
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

// An environment definition's offers reach the parts of its layer (docs/architecture/components.md section 3), which resolve through the same
// engine as workloads into one scope; the descriptor is composed from the parts' exports with the (here faked) node outputs.
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

    private static IReadOnlyList<LoadError> Errors(EnvironmentDefinition definition, EnvironmentDescriptor? baseDescriptor, Catalog catalog) =>
        Resolver.ResolveSubstrate(definition, "definition.yaml", catalog, definition.Over(baseDescriptor), baseDescriptor).Errors;

    private static EnvironmentDefinition Offering(string name, string type, params string[] offers) => new(name, Region, type, offers, null);

    private static async Task<EnvironmentDescriptor> SharedDescriptorAsync() => await ComposeAsync((await PrepareAsync(await DefinitionAsync())).Composer, SharedOutputs);

    // The seed catalog with extra files.
    private static async Task<Catalog> SeedCatalogWithAsync(params (string Path, string Content)[] files)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "catalog");
        var root = Directory.CreateTempSubdirectory("jd-catalog").FullName;
        foreach (var (path, content) in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(f => (Path.GetRelativePath(source, f), File.ReadAllText(f))).Concat(files))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
            await File.WriteAllTextAsync(Path.Combine(root, path), content);
        }

        var loaded = await CatalogDirectory.LoadAsync(root);
        Directory.Delete(root, recursive: true);
        Assert.Empty(loaded.Errors);
        return loaded.Catalog!;
    }

    private static string GoldenSource(string name, [CallerFilePath] string testFile = "") => Path.Combine(Path.GetDirectoryName(testFile)!, "golden", name);

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

    private static async Task<(ResolvedGraph Graph, DescriptorComposer Composer)> PrepareAsync(
        EnvironmentDefinition definition, EnvironmentDescriptor? baseDescriptor = null, Catalog? fixture = null)
    {
        var catalog = fixture ?? await CatalogAsync();
        var environment = definition.Over(baseDescriptor);
        var graph = Resolver.ResolveSubstrate(definition, "definition.yaml", catalog, environment, baseDescriptor);
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
        Assert.Equal(true, Scalar(graph, "cosmos", "disableLocalAuth"));
        Assert.Equal(0.15, Scalar(graph, "workspace", "dailyQuotaGb"));
        Assert.Empty(graph.Exports);
    }

    [Fact]
    public async Task A_read_of_a_part_of_the_same_run_is_evaluated_in_place_into_its_node_references()
    {
        var (graph, _) = await PrepareAsync(await DefinitionAsync());

        // ${shared-group.name} is the export ${group.resourceGroupName}: the read waits on that node, and the graph orders it after it.
        var cosmos = graph.Nodes.Single(n => n.Name == "cosmos");
        Assert.Equal(new Reference(ReferenceKind.Node, "group", "resourceGroupName"), Assert.Single(((Pending)((ConfigText)cosmos.Config["resourceGroupName"]).Result).References));
        Assert.Equal(["@shared/shared/substrate/group"], cosmos.DependsOn);
    }

    [Fact]
    public async Task A_read_of_a_part_deployed_before_comes_from_the_base_descriptor()
    {
        var (graph, _) = await PrepareAsync(await DefinitionAsync("dev.yaml"), await SharedDescriptorAsync());

        Assert.Equal(400L, Scalar(graph, "database", "throughput"));
        Assert.Equal(new Resolved("rg-shared-1"), ((ConfigText)graph.Nodes.Single(n => n.Name == "database").Config["resourceGroupName"]).Result);
        Assert.Equal(["@dev/dev/substrate/group"], graph.Nodes.Single(n => n.Name == "apps").DependsOn);
        // The environment's logs go to the shared workspace by its resource id, read from the base; the setting waits on its environment only.
        var logs = graph.Nodes.Single(n => n.Name == "logs");
        Assert.Equal(new Resolved("/ws/id"), ((ConfigText)logs.Config["workspaceResourceId"]).Result);
        Assert.Equal(new Reference(ReferenceKind.Node, "apps", "environmentId"), Assert.Single(((Pending)((ConfigText)logs.Config["resourceId"]).Result).References));
        Assert.Equal(["@dev/dev/substrate/apps"], logs.DependsOn);
    }

    [Fact]
    public async Task An_environment_deploys_only_environment_parts_and_never_a_shared_resource()
    {
        var (graph, _) = await PrepareAsync(await DefinitionAsync("dev.yaml"), await SharedDescriptorAsync());

        Assert.Equal(["@dev/dev/substrate/apps", "@dev/dev/substrate/database", "@dev/dev/substrate/group", "@dev/dev/substrate/logs"], graph.Nodes.Select(n => n.Id).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(["components/database.yaml", "shared/apps-env.yaml", "shared/env-group.yaml"],
            graph.Nodes.SelectMany(n => n.Provenance.Values).Select(p => p.Source).Distinct().Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task The_substrate_follows_the_offers()
    {
        var shared = await SharedDescriptorAsync();
        var (dev, _) = await PrepareAsync(await DefinitionAsync("dev.yaml", text => text.Replace("container-app, database, monitoring", "container-app, monitoring")), shared);
        var (layer, _) = await PrepareAsync(await DefinitionAsync(edit: text => text.Replace("container-app, database, monitoring", "container-app, monitoring")));

        Assert.Equal(["apps", "group", "logs"], dev.Nodes.Select(n => n.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(["group", "workspace"], layer.Nodes.Select(n => n.Name).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task An_environment_whose_offers_reach_a_shared_resource_its_base_lacks_is_refused_before_anything_is_created()
    {
        var catalog = await CatalogAsync();
        var (layer, composer) = await PrepareAsync(await DefinitionAsync(edit: text => text.Replace("container-app, database, monitoring", "container-app, monitoring")));
        var shared = await ComposeAsync(composer, SharedOutputs.Where(o => !o.Key.EndsWith("/cosmos", StringComparison.Ordinal)).ToDictionary(o => o.Key, o => o.Value));
        Assert.DoesNotContain(layer.Nodes, n => n.Name == "cosmos");

        var error = Assert.Single(Errors(Offering("dev", "dev", "container-app", "database"), shared, catalog));
        var none = Errors(Offering("dev", "dev", "database"), null, catalog);

        Assert.Equal(("definition.yaml", "offers"), (error.File, error.Location));
        Assert.Contains("shared resource 'cosmos-account', but base descriptor 'shared' has no values for it", error.Message);
        Assert.Equal(["cosmos-account", "shared-group"], none.Select(e => e.Message.Split('\'')[1]).ToList());
        Assert.All(none, e => Assert.Contains("but there is no base descriptor", e.Message));
    }

    [Fact]
    public async Task A_part_the_base_already_has_is_refused()
    {
        var shared = await SharedDescriptorAsync();
        var clash = shared with { Values = new Dictionary<string, string>(shared.Values) { ["env-group.name"] = "rg-other" } };

        var error = Assert.Single(Errors(Offering("dev", "dev", "container-app", "database", "monitoring"), clash, await CatalogAsync()));

        Assert.Equal("offers", error.Location);
        Assert.Contains("part 'env-group' is already in the base descriptor's values", error.Message);
    }

    [Fact]
    public async Task Offers_name_declared_components_and_at_most_one_runtime()
    {
        var runtime = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "catalog", "runtimes", "container-app.yaml"));
        var catalog = await SeedCatalogWithAsync(("runtimes/other.yaml", runtime.Replace("name: container-app", "name: other-app")));
        var shared = await SharedDescriptorAsync();

        var unknown = Assert.Single(Errors(Offering("dev", "dev", "container-app", "nosql"), shared, catalog));
        var two = Assert.Single(Errors(Offering("dev", "dev", "container-app", "other-app"), shared, catalog));

        Assert.Equal(("offers[1]", "'nosql' is not a component or runtime in the catalog."), (unknown.Location, unknown.Message));
        Assert.Equal(("offers", "an environment offers at most one runtime; it offers container-app, other-app."), (two.Location, two.Message));
    }

    [Fact]
    public async Task An_offered_component_without_an_implementation_for_the_type_is_an_error()
    {
        var catalog = await SeedCatalogWithAsync(("components/cache.yaml",
            "kind: Component\nname: cache\ndescription: d\nexports: [host]\nimplementations:\n  stage:\n    classes:\n      standard:\n        nodes: { c: { template: t/c, config: { k: 1 } } }\n        exports: { host: x }\n"));

        var error = Assert.Single(Errors(Offering("dev", "dev", "container-app", "cache"), await SharedDescriptorAsync(), catalog));

        Assert.Equal(("offers[1]", "component 'cache' has no implementation for environment type 'dev' and no 'default' implementation."), (error.Location, error.Message));
    }

    [Fact]
    public async Task A_policy_value_on_a_substrate_node_reads_the_parts_of_the_run_like_the_node_does()
    {
        var catalog = await SeedCatalogWithAsync(("policies/apps-name.yaml",
            "kind: Policy\nname: apps-name\nreason: r\nmatch: { template: azure/container-apps-environment }\nset: { environmentName: 'cae-in-${env-group.name}' }\n"));
        var definition = await DefinitionAsync("dev.yaml");
        var shared = await SharedDescriptorAsync();

        var graph = Resolver.ResolveSubstrate(definition, "dev.yaml", catalog, definition.Over(shared), shared);

        Assert.Empty(graph.Errors);
        var name = Assert.IsType<Pending>(((ConfigText)graph.Nodes.Single(n => n.Name == "apps").Config["environmentName"]).Result);
        Assert.Equal(new HashSet<Reference> { new(ReferenceKind.Node, "group", "resourceGroupName") }, name.References);
    }

    [Fact]
    public async Task The_composed_descriptors_hold_every_part_export_by_part_name_and_serve_a_sample_workload()
    {
        var shared = await SharedDescriptorAsync();
        var (_, dev) = await PrepareAsync(await DefinitionAsync("dev.yaml"), shared);

        var descriptor = await ComposeAsync(dev, DevOutputs);

        Assert.Equal(("dev", "southeastasia", "dev"), (descriptor.Name, descriptor.Region, descriptor.Type));
        // A chained export: cosmos-account.resourceGroup is ${shared-group.name}, which is ${group.resourceGroupName}.
        Assert.Equal("rg-shared-1", shared.Values["cosmos-account.resourceGroup"]);
        Assert.Equal("cosmos-1", descriptor.Values["cosmos-account.name"]);
        Assert.Equal("db-dev", descriptor.Values["database.databaseName"]);
        Assert.Equal("rg-dev-1", descriptor.Values["env-group.name"]);
        Assert.Equal("/apps/id", descriptor.Values["apps-env.id"]);
        Assert.Equal("rg-shared-1", descriptor.Values["shared-group.name"]);
        Assert.Equal(["container-app", "database", "monitoring"], descriptor.Offers);

        // The layout serves the components and policies: the sample workload resolves on it without errors.
        var workload = (JObject)YamlSchemaValidator.ParseYaml(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml")));
        Assert.Empty(Resolver.Resolve(workload, "workload.yaml", await CatalogAsync(), descriptor).Errors);
    }

    [Fact]
    public async Task The_descriptor_offers_what_the_definition_offers_and_a_shared_layer_offers_nothing()
    {
        var (_, layer) = await PrepareAsync(await DefinitionAsync());
        var (_, dev) = await PrepareAsync(await DefinitionAsync("dev.yaml"), await SharedDescriptorAsync());

        Assert.Contains("offers: []\n", (await layer.ComposeAsync(Outputs(SharedOutputs))).Yaml);
        Assert.Contains("offers:\n- container-app\n- database\n- monitoring\n", (await dev.ComposeAsync(Outputs(DevOutputs))).Yaml);
    }

    [Fact]
    public async Task The_stage_definition_composes_a_protected_descriptor_with_its_policy_as_the_committed_golden()
    {
        // The golden is in the tests' region; the sample's is the walkthrough's.
        var (_, stage) = await PrepareAsync(await DefinitionAsync("stage.yaml", text => text.Replace("region: southeastasia", $"region: {Region}", StringComparison.Ordinal)), await SharedDescriptorAsync());
        var outputs = DevOutputs.ToDictionary(o => o.Key.Replace("@dev/dev", "@stage/stage", StringComparison.Ordinal), o => o.Value);
        outputs["@stage/stage/substrate/group"] = new() { ["resourceGroupName"] = "rg-stage-1", ["location"] = Region };
        outputs["@stage/stage/substrate/database"] = new() { ["databaseId"] = "/db/id", ["databaseName"] = "db-stage" };

        var composed = await stage.ComposeAsync(Outputs(outputs));

        Assert.Empty(composed.Errors);
        if (System.Environment.GetEnvironmentVariable("JD_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(GoldenSource("stage-environment.yaml"), composed.Yaml);
        }

        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "stage-environment.yaml")), composed.Yaml);
        var descriptor = (await EnvironmentParser.ParseAsync("stage.env.yaml", composed.Yaml!)).Descriptor!;
        Assert.Equal(("stage", "stage"), (descriptor.Name, descriptor.Type));
        Assert.Equal(("dev", 1, true), (descriptor.Approval!.QualifiedOn, descriptor.Approval.Required, descriptor.Approval.SeparateFromDeployer));
        Assert.Equal(["00000000-0000-0000-0000-000000000000"], descriptor.Approval.Approvers);
    }

    [Fact]
    public async Task A_definition_whose_policy_cannot_be_met_is_found_before_anything_is_created()
    {
        var definition = await DefinitionAsync("stage.yaml", text => text.Replace("required: 1", "required: 2", StringComparison.Ordinal));
        var (_, composer) = await PrepareAsync(definition, await SharedDescriptorAsync());

        var error = Assert.Single((await composer.ComposeAsync(null)).Errors);

        Assert.Equal("approval.required", error.Location);
    }

    [Fact]
    public async Task Before_the_deploy_the_exports_are_checked_and_nothing_needs_outputs()
    {
        var (_, composer) = await PrepareAsync(await DefinitionAsync());

        var composed = await composer.ComposeAsync(null);

        Assert.Empty(composed.Errors);
        Assert.Contains("endpoint: ${cosmos.documentEndpoint}", composed.Yaml);
    }

    [Fact]
    public async Task An_export_without_a_usable_output_is_an_error_after_the_deploy()
    {
        var (_, composer) = await PrepareAsync(await DefinitionAsync());
        var outputs = SharedOutputs.Where(o => !o.Key.EndsWith("/cosmos", StringComparison.Ordinal)).ToDictionary(o => o.Key, o => o.Value);

        var composed = await composer.ComposeAsync(Outputs(outputs));

        Assert.Null(composed.Yaml);
        Assert.Contains(composed.Errors, e => e.Location == "values.cosmos-account.endpoint" && e.Message.Contains("no usable deployed output"));
    }

    [Fact]
    public async Task An_export_reading_a_secret_or_null_output_directly_or_chained_fails_and_no_descriptor_is_written()
    {
        var (_, composer) = await PrepareAsync(await DefinitionAsync());
        var outputs = Outputs(SharedOutputs).ToDictionary(o => o.Key, o => (IReadOnlyDictionary<string, ConfigEntry>)new Dictionary<string, ConfigEntry>(o.Value));
        outputs["@shared/shared/substrate/cosmos"] = new Dictionary<string, ConfigEntry>(outputs["@shared/shared/substrate/cosmos"]) { ["documentEndpoint"] = new("s3cr3t-endpoint", isSecret: true) };
        outputs["@shared/shared/substrate/workspace"] = new Dictionary<string, ConfigEntry>(outputs["@shared/shared/substrate/workspace"]) { ["workspaceName"] = new(null) };
        outputs["@shared/shared/substrate/group"] = new Dictionary<string, ConfigEntry>(outputs["@shared/shared/substrate/group"]) { ["resourceGroupName"] = new("s3cr3t-group", isSecret: true) };

        var composed = await composer.ComposeAsync(outputs);

        Assert.Null(composed.Yaml);
        Assert.Equal(
            ["values.cosmos-account.endpoint", "values.cosmos-account.resourceGroup", "values.log-analytics.name", "values.log-analytics.resourceGroup", "values.shared-group.name"],
            composed.Errors.Select(e => e.Location).Order(StringComparer.Ordinal).ToList());
        Assert.All(composed.Errors, e => Assert.Contains("missing, null or secret", e.Message));
        Assert.DoesNotContain("s3cr3t", string.Join('\n', composed.Errors.Select(e => e.ToString())));
    }

    [Fact]
    public async Task A_workload_named_like_the_environment_shares_no_id_stack_or_generated_name_with_its_substrate()
    {
        var files = new[]
        {
            "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment } }\n",
            "kind: Naming\nrules:\n  rg:\n    pattern: \"rg-{env}-{hash}\"\n    maxLength: 90\n    allowed: \"[a-z0-9-]\"\n",
            "kind: Component\nname: thing\ndescription: d\nexports: [out]\nimplementations:\n  default:\n    environment:\n      nodes:\n        group:\n          template: t/group\n          config:\n            groupName: ${name('rg')}\n      exports: {}\n    classes:\n      substrate:\n        nodes:\n          group:\n            template: t/group\n            config:\n              groupName: ${name('rg')}\n        exports:\n          out: ${group.groupName}\n",
        }.Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var loaded = await CatalogParser.ParseAsync(files);
        Assert.Empty(loaded.Errors);
        var definition = Offering("dev", "dev", "thing");
        var environment = definition.Over(null);
        var workload = (JObject)YamlSchemaValidator.ParseYaml("metadata: { name: dev, team: crew }\nrequires:\n  - type: thing\n    id: substrate\n    class: substrate\n");

        var substrate = Resolver.ResolveSubstrate(definition, "definition.yaml", loaded.Catalog!, environment, null).Nodes.Single();
        var workloadNode = Resolver.Resolve(workload, "workload.yaml", loaded.Catalog!, environment).Nodes.Single();

        Assert.Equal(("@dev/dev/substrate/group", "_dev.dev.substrate.group"), (substrate.Id, substrate.Stack));
        Assert.Equal(("dev/dev/substrate/group", "dev.dev.substrate.group"), (workloadNode.Id, workloadNode.Stack));
        Assert.NotEqual(((ConfigText)substrate.Config["groupName"]).Result, ((ConfigText)workloadNode.Config["groupName"]).Result);
    }

    [Fact]
    public async Task The_composed_descriptor_must_pass_the_descriptor_rules()
    {
        // A part named like a top-level field resolves, but its exports cannot be values (values.region would shadow env.region).
        var catalog = await SeedCatalogWithAsync(
            ("shared/region.yaml", "kind: SharedResource\nname: region\ndescription: d\nscope: environment\nnodes:\n  spot:\n    template: t/spot\n    config: { v: '${env.region}' }\nexports: { id: '${spot.id}' }\n"),
            ("components/regional.yaml", "kind: Component\nname: regional\ndescription: d\nexports: [id]\nuses: [region]\nimplementations:\n  default:\n    classes:\n      standard:\n        nodes:\n          thing: { template: t/thing, config: { v: '${region.id}' } }\n        exports: { id: '${thing.id}' }\n"));
        var definition = await DefinitionAsync("dev.yaml", text => text.Replace("offers: [container-app, database, monitoring]", "offers: [container-app, database, monitoring, regional]"));
        var (_, composer) = await PrepareAsync(definition, await SharedDescriptorAsync(), catalog);

        Assert.Contains((await composer.ComposeAsync(null)).Errors, e => e.Location == "values.region");
    }

    [Fact]
    public async Task The_definitions_region_is_the_descriptors_and_a_definition_without_one_is_a_schema_error()
    {
        var (_, dev) = await PrepareAsync(await DefinitionAsync("dev.yaml", text => text.Replace("region: southeastasia", "region: region-9", StringComparison.Ordinal)), await SharedDescriptorAsync());
        var directory = Directory.CreateTempSubdirectory("jd-def").FullName;
        try
        {
            var path = Path.Combine(directory, "dev.yaml");
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(Sample("dev.yaml"))).Replace("region: southeastasia\n", string.Empty, StringComparison.Ordinal));

            Assert.Equal("region-9", (await ComposeAsync(dev, DevOutputs)).Region);
            var loaded = await EnvironmentDefinitionFile.LoadAsync(path);
            Assert.Null(loaded.Definition);
            Assert.Contains(loaded.Errors, e => e.File == path && e.Message.Contains("region", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Definition_rules_are_reported_with_the_file()
    {
        var dir = Directory.CreateTempSubdirectory("jd-def").FullName;
        var path = Path.Combine(dir, "d.yaml");
        await File.WriteAllTextAsync(path, "kind: EnvironmentDefinition\nname: xyz\ntype: dev\nrequires: [{ type: env-substrate }]\nvalues: {}\n");
        var old = await EnvironmentDefinitionFile.LoadAsync(path);

        Assert.Contains(old.Errors, e => e.File == path && e.Message.Contains("offers"));
        Assert.Contains("not found", Assert.Single((await EnvironmentDefinitionFile.LoadAsync(Path.Combine(dir, "none.yaml"))).Errors).Message);
    }

    [Fact]
    public async Task An_environment_has_no_runtime_so_a_part_reading_it_is_an_error_and_workload_scope_policies_add_nothing()
    {
        var files = new[]
        {
            "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment } }\n",
            "kind: SharedResource\nname: thing\ndescription: d\nscope: environment\nnodes:\n  a:\n    template: t/a\n    config: { who: '${runtime.principalId}' }\nexports: {}\n",
            TestCatalog.Component("user", "[out]", "nodes:\n  b:\n    template: t/b\n    config: { k: 1 }\nexports:\n  out: x\n").Replace("exports: [out]\n", "exports: [out]\nuses: [thing]\n"),
        }.Select((content, i) => new CatalogSource($"f{i}.yaml", content));
        var loaded = await CatalogParser.ParseAsync(files);
        Assert.Empty(loaded.Errors);

        var graph = Resolver.ResolveSubstrate(Offering("dev", "dev", "user"), "definition.yaml", loaded.Catalog!, Offering("dev", "dev", "user").Over(null), null);
        var (layer, _) = await PrepareAsync(await DefinitionAsync());

        var error = Assert.Single(graph.Errors);
        Assert.Equal(("f1.yaml", "who"), (error.File, error.Location));
        Assert.Contains("references runtime.principalId, but there is no runtime", error.Message);
        Assert.DoesNotContain(layer.Nodes, n => n.Scope == GraphBuilder.WorkloadScope);
    }
}
