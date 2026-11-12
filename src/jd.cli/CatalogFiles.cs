using jd.resolver;

namespace jd.cli;

/// <summary>
/// A catalog directory as the command line read it: every <c>*.yaml</c> under it, recursively, as the <see cref="SourceCatalog"/> the use cases take,
/// each named relative to the directory as the catalog layout names it; and the errors that name one of those files, given the path a person can open.
/// </summary>
internal sealed class CatalogFiles
{
    private readonly string _directory;
    private readonly HashSet<string> _names;

    private CatalogFiles(string directory, HashSet<string> names, SourceCatalog source)
    {
        _directory = directory;
        _names = names;
        Source = source;
    }

    /// <summary>The catalog, named <c>directory</c> as typed.</summary>
    public SourceCatalog Source { get; }

    /// <summary>Reads every <c>*.yaml</c> under <paramref name="directory"/>, which exists.</summary>
    public static async Task<CatalogFiles> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var documents = new List<SourceDocument>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
            names.Add(name);
            documents.Add(new SourceDocument(name, await File.ReadAllBytesAsync(file, cancellationToken)));
        }

        return new CatalogFiles(directory, names, new SourceCatalog(directory, documents));
    }

    /// <summary>The errors, each that names a file of the catalog renamed to its path under the directory; any other is left as it is.</summary>
    public IEnumerable<LoadError> Located(IEnumerable<LoadError> errors) =>
        errors.Select(e => _names.Contains(e.File) ? e with { File = Path.Combine(_directory, e.File) } : e);
}
