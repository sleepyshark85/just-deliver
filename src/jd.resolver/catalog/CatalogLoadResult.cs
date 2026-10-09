namespace jd.resolver.catalog;

/// <summary>A catalog file: its path relative to the catalog root and its YAML text.</summary>
public sealed record CatalogSource(string Path, string Content);

/// <param name="File">Catalog-relative path of the offending file.</param>
/// <param name="Location">Where in the file, for example <c>nodes.database.config</c>; empty for the document as a whole.</param>
/// <param name="Message">What is wrong and, where useful, what to do.</param>
public sealed record CatalogError(string File, string Location, string Message)
{
    public override string ToString() => Location.Length == 0 ? $"{File}: {Message}" : $"{File}: {Location}: {Message}";
}

/// <summary><see cref="Catalog"/> is set exactly when <see cref="Errors"/> is empty.</summary>
public sealed record CatalogLoadResult(Catalog? Catalog, IReadOnlyList<CatalogError> Errors);
