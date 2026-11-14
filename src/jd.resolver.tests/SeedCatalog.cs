using jd.resolver.catalog;

namespace jd.resolver.tests;

// The seed catalog, read as the command line reads a catalog: every *.yaml under it, named relative to it.
internal static class SeedCatalog
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "catalog");

    public static SourceCatalog Read() => new(
        Root,
        [.. Directory.EnumerateFiles(Root, "*.yaml", SearchOption.AllDirectories)
            .Select(file => new SourceDocument(Path.GetRelativePath(Root, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes(file)))]);

    public static Task<LoadResult<Catalog>> LoadAsync() => CatalogLoader.LoadAsync(Read());
}
