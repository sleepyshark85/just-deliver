namespace jd.resolver.catalog;

/// <summary>A catalog file: its path relative to the catalog root and its YAML text.</summary>
public sealed record CatalogSource(string Path, string Content);

/// <summary><see cref="Catalog"/> is set exactly when <see cref="Errors"/> is empty.</summary>
public sealed record CatalogLoadResult(Catalog? Catalog, IReadOnlyList<LoadError> Errors);
