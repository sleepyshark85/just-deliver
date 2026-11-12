using jd.bp.pulumi;
using jd.orchestrator;
using jd.resolver;
using jd.resolver.graph;

namespace jd.cli;

/// <summary>
/// <see cref="ITemplates"/> over the Pulumi template library, from a catalog's templates part: its template files, each named by its directory
/// (docs/architecture/application.md section 5). A part with no documents is a templates directory that is missing, or empty.
/// </summary>
internal sealed class PulumiTemplates : ITemplates
{
    private const string TemplateSuffix = "/" + TemplateLibrary.TemplateFile;

    private readonly TemplateLibrary _library;
    private readonly IReadOnlyList<LoadError> _missing;

    private PulumiTemplates(TemplateLibrary library, IReadOnlyList<LoadError> missing)
    {
        _library = library;
        _missing = missing;
    }

    /// <summary>The templates of <paramref name="part"/>; the documents that are no template file are not read.</summary>
    public static PulumiTemplates Of(SourceCatalog part) => new(
        TemplateLibrary.Parse(part.Documents.Where(d => d.Name.EndsWith(TemplateSuffix, StringComparison.Ordinal)).Select(d => (d.Name[..^TemplateSuffix.Length], d.Text()))),
        part.Documents.Count == 0 ? [new LoadError(part.Name, string.Empty, "template directory not found.")] : []);

    /// <inheritdoc/>
    public string GetContent(string templateName) => _library.GetContent(templateName);

    /// <inheritdoc/>
    public IReadOnlyList<LoadError> Check(ResolvedGraph graph) => [.. _missing, .. _library.Check(graph)];
}
