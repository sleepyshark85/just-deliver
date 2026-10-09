namespace jd.resolver;

/// <param name="File">Path of the offending file (catalog-relative for catalog files).</param>
/// <param name="Location">Where in the file, for example <c>nodes.database.config</c>; empty for the document as a whole.</param>
/// <param name="Message">What is wrong and, where useful, what to do.</param>
public sealed record LoadError(string File, string Location, string Message)
{
    public override string ToString() => Location.Length == 0 ? $"{File}: {Message}" : $"{File}: {Location}: {Message}";
}
