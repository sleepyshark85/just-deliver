using jd.resolver.catalog;
using Xunit;

namespace jd.resolver.tests;

public class SeedCatalogTests
{
    [Fact]
    public async Task Seed_catalog_loads_with_no_errors()
    {
        var result = await SeedCatalog.LoadAsync();

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Value);
    }

    // The shared resources and database's environment part (docs/architecture/components.md section 2) are loaded and checked, and
    // nothing consumes them yet (S21e). A chained read passes the checks: database's environment part reads cosmos-account.resourceGroup,
    // which is shared-group.name.
    [Fact]
    public async Task Seed_catalog_declares_the_shared_resources_and_database_environment_part()
    {
        var catalog = Assert.IsType<Catalog>((await SeedCatalog.LoadAsync()).Value);

        Assert.Equal(
            [("apps-env", "environment", "env-group,log-analytics"), ("cosmos-account", "shared", "shared-group"), ("env-group", "environment", ""),
                ("log-analytics", "shared", "shared-group"), ("shared-group", "shared", "")],
            catalog.SharedResources.Values.Select(s => (s.Name, Assert.Single(s.Parts).Scope, string.Join(',', s.Reads))).OrderBy(s => s.Name, StringComparer.Ordinal));
        Assert.Equal("${shared-group.name}", catalog.SharedResources["cosmos-account"].Parts[0].Exports["resourceGroup"]);
        var database = catalog.Components["database"];
        Assert.Equal(["cosmos-account"], database.Reads);
        var environment = TestCatalog.PartAt(database, "implementations.default.environment");
        Assert.Equal("${cosmos-account.resourceGroup}", (string?)environment.Nodes["database"].Config["resourceGroupName"]);
        Assert.Equal(["databaseName", "databaseId"], environment.Exports.Keys);
    }

    // The templates are a part of their own (docs/architecture/application.md section 3): a template file is no catalog document.
    [Fact]
    public async Task Templates_directory_is_not_loaded()
    {
        var result = await CatalogLoader.LoadAsync(new SourceCatalog("catalog",
        [
            new("catalog.yaml", "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment }, stage: { layer: environment, approval: true } }\n"u8.ToArray()),
            new("templates/azure/Pulumi.yaml", "name: not-a-catalog-document\n"u8.ToArray()),
        ]));

        Assert.Empty(result.Errors);
    }
}
