using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.graph;
using jd.resolver.policies;
using Newtonsoft.Json.Linq;

namespace jd.resolver;

/// <summary>The whole resolution in one call: expand the workload, apply the policies, build the graph. Pure.</summary>
public static class Resolver
{
    /// <param name="workload">The workload definition, already schema-validated (see <see cref="workload.WorkloadFile"/>).</param>
    /// <param name="workloadFile">Name reported in errors about the workload itself.</param>
    /// <param name="catalog">The loaded catalog.</param>
    /// <param name="environment">The environment descriptor the graph is resolved for.</param>
    public static ResolvedGraph Resolve(JObject workload, string workloadFile, Catalog catalog, EnvironmentDescriptor environment) =>
        new GraphBuilder(environment).Build(new PolicyApplier(catalog, environment).Apply(new Expander(catalog, environment).Expand(workload, workloadFile)));
}
