namespace jd.resolver.catalog;

/// <summary>A catalog file: its path relative to the catalog root and its YAML text.</summary>
public sealed record CatalogSource(string Path, string Content);
