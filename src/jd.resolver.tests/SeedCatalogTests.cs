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
            await File.WriteAllTextAsync(Path.Combine(root, "catalog.yaml"), "kind: Catalog\nversion: \"1\"\n");
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
