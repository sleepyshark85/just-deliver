namespace jd.resolver;

/// <summary>A catalog handed to jd as its documents (docs/architecture/application.md section 3).</summary>
/// <param name="Name">The catalog as the client names it: the CLI passes the directory as typed.</param>
/// <param name="Documents">Every catalog document, each named relative to the catalog.</param>
public sealed record SourceCatalog(string Name, IReadOnlyList<SourceDocument> Documents);
