namespace jd.resolver.catalog;

/// <summary>Reads a catalog directory from disk and hands its documents to <see cref="CatalogParser"/>.</summary>
public static class CatalogDirectory
{
    /// <summary>Loads every catalog file under <paramref name="root"/>, the templates aside.</summary>
    public static async Task<LoadResult<Catalog>> LoadAsync(string root, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
        {
            return new LoadResult<Catalog>(null, [new LoadError(root, string.Empty, "catalog directory not found.")]);
        }

        var documents = new List<SourceDocument>();
        foreach (var file in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            documents.Add(new SourceDocument(relative, await File.ReadAllBytesAsync(file, cancellationToken)));
        }

        return await CatalogParser.ParseAsync(new SourceCatalog(root, documents), cancellationToken);
    }
}
