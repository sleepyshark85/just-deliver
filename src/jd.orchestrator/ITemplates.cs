using jd.core.bp;
using jd.resolver;
using jd.resolver.graph;

namespace jd.orchestrator;

/// <summary>
/// A catalog's deployment templates, built per request from its templates part (docs/architecture/application.md section 5): what the backend
/// deploys a node with, and the offline check that a graph fits them before anything is created.
/// </summary>
public interface ITemplates : ITemplateStore
{
    /// <summary>Every way <paramref name="graph"/> does not fit the templates, and every template that cannot be read; empty when it fits.</summary>
    IReadOnlyList<LoadError> Check(ResolvedGraph graph);
}
