namespace jd.resolver;

/// <summary>
/// A catalog handed to jd as it was read: its name and every document in it (docs/architecture/application.md section 3). The catalog loader
/// and the templates each take their part of it.
/// </summary>
/// <param name="Name">The name its parts and errors are named under: the CLI passes the catalog argument as typed.</param>
/// <param name="Documents">Every document, named by its path relative to the catalog, with <c>/</c> between segments.</param>
public sealed record SourceCatalog(string Name, IReadOnlyList<SourceDocument> Documents)
{
    /// <summary>
    /// The catalog loader's <paramref name="errors"/>, each that names one of the documents renamed to its path under <see cref="Name"/>, the path a
    /// person opens; an error about the catalog as a whole is left as it is.
    /// </summary>
    public IEnumerable<LoadError> Located(IEnumerable<LoadError> errors)
    {
        var names = Documents.Select(document => document.Name).ToHashSet(StringComparer.Ordinal);
        return errors.Select(error => names.Contains(error.File) ? error with { File = Path.Combine(Name, error.File) } : error);
    }
}
