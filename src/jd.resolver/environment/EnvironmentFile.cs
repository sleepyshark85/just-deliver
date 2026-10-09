using jd.resolver.catalog;

namespace jd.resolver.environment;

/// <summary>Reads an environment descriptor file from disk and hands its text to <see cref="EnvironmentParser"/>.</summary>
public static class EnvironmentFile
{
    public static async Task<EnvironmentLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new EnvironmentLoadResult(null, [new CatalogError(path, string.Empty, "environment descriptor not found.")]);
        }

        return await EnvironmentParser.ParseAsync(path, await File.ReadAllTextAsync(path, cancellationToken), cancellationToken);
    }
}
