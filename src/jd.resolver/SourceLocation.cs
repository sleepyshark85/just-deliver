namespace jd.resolver;

/// <summary>
/// Where a value is written, as the binder read it: the file (catalog-relative for catalog files) and the location in it, as a
/// <see cref="LoadError"/> names them.
/// </summary>
public sealed record SourceLocation(string File, string Location);
