using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.workload;
using Newtonsoft.Json.Linq;

namespace jd.orchestrator.usecases;

// What preview and deploy resolve: the workload, the catalog and the environment, each loaded from its document, the environment's type checked
// against the catalog. WorkloadName names the workload's document as its errors do.
internal sealed record WorkloadInputs(JObject Workload, string WorkloadName, Catalog Catalog, EnvironmentDescriptor Environment)
{
    // Null with the errors in the order a person fixes them: the workload's alone, then the catalog's and the environment's together, then the type's.
    public static async Task<LoadResult<WorkloadInputs>> LoadAsync(SourceDocument workload, SourceDocument environment, SourceCatalog catalog, CancellationToken cancellationToken)
    {
        var loaded = await WorkloadFile.LoadAsync(workload, cancellationToken);
        if (loaded.Value is not { } definition)
        {
            return new(null, loaded.Errors);
        }

        var catalogResult = await CatalogParser.ParseAsync(catalog, cancellationToken);
        var environmentResult = await EnvironmentParser.LoadAsync(environment, cancellationToken);
        if (catalogResult.Value is not { } parsed || environmentResult.Value is not { } descriptor)
        {
            return new(null, [.. catalogResult.Errors, .. environmentResult.Errors]);
        }

        var typeErrors = Checks.EnvironmentType(parsed, descriptor, environment.Name);
        return typeErrors.Count > 0 ? new(null, typeErrors) : new(new WorkloadInputs(definition, workload.Name, parsed, descriptor), []);
    }

    public ResolvedGraph Resolve() => Resolver.Resolve(Workload, WorkloadName, Catalog, Environment);
}
