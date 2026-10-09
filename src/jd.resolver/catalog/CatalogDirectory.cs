namespace jd.resolver.catalog;

/// <summary>Reads a catalog directory from disk and hands its files to <see cref="CatalogParser"/>.</summary>
public static class CatalogDirectory
{
    /// <summary>Templates are Pulumi YAML, not catalog documents; they are not loaded here. Part of the catalog layout.</summary>
    public const string TemplatesDirectory = "templates";

    public static async Task<CatalogLoadResult> LoadAsync(string root, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
        {
            return new CatalogLoadResult(null, [new LoadError(root, string.Empty, "catalog directory not found.")]);
        }

        var sources = new List<CatalogSource>();
        foreach (var file in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith(TemplatesDirectory + "/", StringComparison.Ordinal))
            {
                continue;
            }

            sources.Add(new CatalogSource(relative, await File.ReadAllTextAsync(file, cancellationToken)));
        }

        return await CatalogParser.ParseAsync(sources, cancellationToken);
    }
}
