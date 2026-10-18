using jd.resolver.catalog;
using Xunit;

namespace jd.resolver.tests;

public class SeedCatalogTests
{
    [Fact]
    public async Task Seed_catalog_loads_with_no_errors()
    {
        var result = await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog"));

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Catalog);
    }

    // The shared resources and database's environment part (docs/architecture/components.md section 2) are loaded and checked, and
    // nothing consumes them yet (S21e). A chained read passes the checks: database's environment part reads cosmos-account.resourceGroup,
    // which is shared-group.name.
    [Fact]
    public async Task Seed_catalog_declares_the_shared_resources_and_database_environment_part()
    {
        var catalog = Assert.IsType<Catalog>((await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog"))).Catalog);

        Assert.Equal(
            [("apps-env", SharedScope.Environment, "env-group,log-analytics"), ("cosmos-account", SharedScope.Shared, "shared-group"), ("env-group", SharedScope.Environment, ""),
                ("log-analytics", SharedScope.Shared, "shared-group"), ("shared-group", SharedScope.Shared, "")],
            catalog.SharedResources.Values.Select(s => (s.Name, s.Scope, string.Join(',', s.Uses))).OrderBy(s => s.Name, StringComparer.Ordinal));
        Assert.Equal("${shared-group.name}", catalog.SharedResources["cosmos-account"].Part.Exports["resourceGroup"]);
        var database = catalog.Components["database"];
        Assert.Equal(["cosmos-account"], database.Uses);
        var environment = Assert.IsType<Part>(database.Implementations["default"].Environment);
        Assert.Equal("${cosmos-account.resourceGroup}", (string?)environment.Nodes["database"].Config["resourceGroupName"]);
        Assert.Equal(["databaseName", "databaseId"], environment.Exports.Keys);
    }

    [Fact]
    public async Task Missing_directory_is_an_error()
    {
        var result = await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "no-such-catalog"));

        Assert.Contains("not found", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Templates_directory_is_not_loaded()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "catalog.yaml"), "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: {}, stage: { approval: true } }\n");
            Directory.CreateDirectory(Path.Combine(root, "templates", "azure"));
            await File.WriteAllTextAsync(Path.Combine(root, "templates", "azure", "Pulumi.yaml"), "name: not-a-catalog-document\n");

            var result = await CatalogDirectory.LoadAsync(root);

            Assert.Empty(result.Errors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
