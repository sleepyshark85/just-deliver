using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;

namespace jd.orchestrator.usecases;

// The walk deploy and env up share: the graph checked against the catalog's templates, then the orchestrator built over the backend, each node
// reported as it finishes.
internal static class Walk
{
    // The orchestrator, or null with every way the graph does not fit the templates; the backend is built only when it fits.
    public static (Orchestrator? Orchestrator, IReadOnlyList<LoadError> Misfits) Prepare(
        SourceCatalog source, ResolvedGraph graph, Catalog catalog, EnvironmentDescriptor environment, Connections connections)
    {
        var (templates, misfits) = Checks.Templates(source, [graph], connections);
        return misfits.Count > 0 ? (null, misfits) : (new Orchestrator(connections.Backend(), templates, catalog, environment, connections.Probe()), []);
    }

    // The orchestrator's node callback, as progress.
    public static Action<NodeReport> Reporting(Action<Progress>? progress) => report => progress?.Invoke(new NodeFinished(report));
}
