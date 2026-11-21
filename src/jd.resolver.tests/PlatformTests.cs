using System.Text;
using jd.resolver.catalog;
using jd.resolver.environment;
using Xunit;

namespace jd.resolver.tests;

// The platform in the model (docs/architecture/multi-cloud.md section 1, ADR 0021): an environment gets only its own platform's
// implementations and shared resources, with no fallback, and documents of the shape before platforms say what to add.
public class PlatformTests
{
    private const string Region = "region-1";

    // The seed catalog with a second platform declared, each file passed through edit, and the extra documents.
    private static SourceCatalog TwoPlatforms(Func<string, string, string>? edit = null, params (string Name, string Text)[] extra)
    {
        var seed = SeedCatalog.Read();
        string Edited(SourceDocument document)
        {
            var text = document.Name == "catalog.yaml" ? document.Text().Replace("platforms: [azure]", "platforms: [azure, gcp]", StringComparison.Ordinal) : document.Text();
            return edit is null ? text : edit(document.Name, text);
        }

        return new SourceCatalog(seed.Name, [
            .. seed.Documents.Select(document => new SourceDocument(document.Name, Encoding.UTF8.GetBytes(Edited(document)))),
            .. extra.Select(document => new SourceDocument(document.Name, Encoding.UTF8.GetBytes(document.Text))),
        ]);
    }

    private static async Task<Catalog> LoadAsync(SourceCatalog source)
    {
        var loaded = await CatalogLoader.LoadAsync(source);
        Assert.Empty(loaded.Errors);
        return loaded.Value!;
    }

    private static EnvironmentDescriptor Environment(string type, string platform, IReadOnlyList<string> offers) =>
        new("dev", Region, type, platform, new Dictionary<string, string>(), offers, null);

    private const string GcpBucket =
        "kind: SharedResource\nname: gcp-bucket\ndescription: d\nplatform: gcp\nscope: shared\nnodes:\n  bucket:\n    template: gcp/bucket\n    config: { name: b }\nexports:\n  name: ${bucket.name}\n";

    [Fact]
    public async Task A_workload_on_a_platform_without_an_implementation_gets_no_other_platforms_implementation()
    {
        var catalog = await LoadAsync(TwoPlatforms());
        var workload = await TestWorkload.ParseAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml")));

        var graph = Resolver.Resolve(workload, "workload.yaml", catalog, Environment("dev", "gcp", ["container-app", "database", "monitoring"]));

        Assert.Empty(graph.Nodes);
        Assert.Contains(graph.Errors, error => error.Message.Contains("component 'database' has no implementations entry for 'gcp' (keys: azure).", StringComparison.Ordinal));
        Assert.Contains(graph.Errors, error => error.Message == "runtime 'container-app' has no implementations entry for 'gcp' (keys: azure).");
    }

    [Fact]
    public async Task An_environment_of_another_platform_reaches_none_of_the_shared_resources_another_platform_uses()
    {
        var catalog = await LoadAsync(TwoPlatforms());

        var graph = Resolver.ResolveSubstrate("shared.yaml", catalog, Environment("shared", "gcp", ["container-app", "database", "monitoring"]), null);

        Assert.Empty(graph.Errors);
        Assert.Empty(graph.Nodes);
    }

    [Fact]
    public async Task An_environment_layer_of_another_platform_is_refused_the_offered_components_implementation()
    {
        var catalog = await LoadAsync(TwoPlatforms());

        var graph = Resolver.ResolveSubstrate("dev.yaml", catalog, Environment("dev", "gcp", ["database"]), null);

        var error = Assert.Single(graph.Errors);
        Assert.Equal("component 'database' has no implementations entry for 'gcp' (keys: azure).", error.Message);
        Assert.Empty(graph.Nodes);
    }

    [Fact]
    public async Task An_environment_of_an_undeclared_platform_is_refused_naming_the_declared_ones()
    {
        var catalog = (await SeedCatalog.LoadAsync()).Value!;
        var definition = (await EnvironmentParser.ParseAsync("env.yaml", "kind: Environment\nname: dev\nregion: r\ntype: dev\nplatform: gcp\noffers: [database]\nvalues: {}\n")).Value!;

        var error = Assert.Single(EnvironmentTypeCheck.Check(catalog, definition, "env.yaml"));

        Assert.Equal(new LoadError("env.yaml", "platform", "'gcp' is not a platform of the catalog (declared: azure)."), error);
    }

    [Theory]
    [InlineData("components/database.yaml", "uses: [cosmos-account]", "uses: [cosmos-account, gcp-bucket]", "implementations.azure.uses", "azure")]
    [InlineData("runtimes/container-app.yaml", "uses: [env-group, apps-env]", "uses: [env-group, apps-env, gcp-bucket]", "implementations.azure.uses", "azure")]
    [InlineData("shared/log-analytics.yaml", "uses: [shared-group]", "uses: [shared-group, gcp-bucket]", "uses", "azure")]
    public async Task A_part_using_a_shared_resource_of_another_platform_is_refused(string file, string uses, string withOther, string location, string platform)
    {
        var loaded = await CatalogLoader.LoadAsync(TwoPlatforms((name, text) => name == file ? text.Replace(uses, withOther, StringComparison.Ordinal) : text, ("shared/gcp-bucket.yaml", GcpBucket)));

        var error = Assert.Single(loaded.Errors);
        Assert.Equal(new LoadError(file, location, $"'gcp-bucket' is a shared resource of platform 'gcp'; a part of platform '{platform}' uses only its own platform's."), error);
        Assert.Null(loaded.Value);
    }

    [Fact]
    public async Task A_base_descriptor_of_another_platform_is_refused()
    {
        var catalog = await LoadAsync(TwoPlatforms());
        var shared = Environment("shared", "gcp", []) with { Name = "shared", Values = new Dictionary<string, string> { ["cosmos-account.id"] = "/c" } };
        var definition = (await EnvironmentParser.LoadDefinitionAsync(
            new SourceDocument("dev.yaml", Encoding.UTF8.GetBytes("kind: EnvironmentDefinition\nname: dev\nregion: r\ntype: dev\nplatform: azure\noffers: [database]\n")),
            CancellationToken.None)).Value!;

        var graph = Resolver.ResolveSubstrate("dev.yaml", catalog, definition.Over(shared), shared);

        Assert.Contains(new LoadError("dev.yaml", "platform",
            "the environment's platform is 'azure', but base descriptor 'shared' is of platform 'gcp'; pass the descriptor of a shared layer of the same platform."), graph.Errors);
        Assert.Empty(graph.Nodes);
    }

    [Theory]
    [InlineData("catalog.yaml", "platforms: [azure, gcp]\n", "", "a catalog written for an older jd lacks it, so add it and key each component's and runtime's implementations by platform")]
    [InlineData("shared/env-group.yaml", "platform: azure\n", "", "a catalog written for an older jd lacks it, so add it (see catalog/shared/cosmos-account.yaml)")]
    [InlineData("components/monitoring.yaml", "implementations:\n  azure:\n    uses: [env-group, log-analytics]\n", "uses: [env-group, log-analytics]\nimplementations:\n  azure:\n", "uses belongs in each platform entry of implementations")]
    public async Task A_catalog_document_of_the_shape_before_platforms_says_what_to_add(string file, string current, string old, string message)
    {
        var loaded = await CatalogLoader.LoadAsync(TwoPlatforms((name, text) => name == file ? text.Replace(current, old, StringComparison.Ordinal) : text));

        Assert.Contains(loaded.Errors, error => error.File == file && error.Message.Contains(message, StringComparison.Ordinal));
        Assert.Null(loaded.Value);
    }

    [Fact]
    public async Task Implementations_keyed_by_environment_type_say_to_put_them_under_a_platform()
    {
        const string old = "kind: Runtime\nname: old-runtime\ndescription: d\nimplementations:\n  default:\n    nodes: {}\n";

        var loaded = await CatalogLoader.LoadAsync(TwoPlatforms(extra: ("runtimes/old-runtime.yaml", old)));

        Assert.Contains(loaded.Errors, error => error is { File: "runtimes/old-runtime.yaml", Location: "implementations.default.nodes" }
            && error.Message.StartsWith("not a field of a platform entry (uses, types); a catalog written for an older jd keys implementations by environment type", StringComparison.Ordinal));
        Assert.Null(loaded.Value);
    }

    [Fact]
    public async Task A_definition_without_a_platform_says_what_to_add()
    {
        var loaded = await EnvironmentParser.LoadDefinitionAsync(
            new SourceDocument("dev.yaml", Encoding.UTF8.GetBytes("kind: EnvironmentDefinition\nname: dev\nregion: r\ntype: dev\noffers: [database]\n")), CancellationToken.None);

        var error = Assert.Single(loaded.Errors);
        Assert.Equal("dev.yaml", error.File);
        Assert.Contains("a definition written for an older jd lacks it, so add it", error.Message, StringComparison.Ordinal);
        Assert.Null(loaded.Value);
    }

    [Fact]
    public async Task A_descriptor_without_a_platform_is_refused_until_env_up_rewrites_it()
    {
        var loaded = await EnvironmentParser.ParseAsync("dev.env.yaml", "kind: Environment\nname: dev\nregion: r\ntype: dev\noffers: [database]\nvalues: {}\n");

        var error = Assert.Single(loaded.Errors);
        Assert.Equal("dev.env.yaml", error.File);
        Assert.Contains("re-run `jd env up`", error.Message, StringComparison.Ordinal);
        Assert.Null(loaded.Value);
    }
}
