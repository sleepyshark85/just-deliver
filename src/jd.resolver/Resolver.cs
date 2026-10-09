using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.graph;
using jd.resolver.policies;
using Newtonsoft.Json.Linq;

namespace jd.resolver;

/// <summary>The whole resolution in one call: expand the requirements, apply the policies, build the graph. Pure.</summary>
public static class Resolver
{
    /// <param name="workload">The workload definition, already schema-validated (see <see cref="workload.WorkloadFile"/>).</param>
    /// <param name="workloadFile">Name reported in errors about the workload itself.</param>
    /// <param name="catalog">The loaded catalog.</param>
    /// <param name="environment">The environment descriptor the graph is resolved for.</param>
    public static ResolvedGraph Resolve(JObject workload, string workloadFile, Catalog catalog, EnvironmentDescriptor environment) =>
        Build(new Expander(catalog, environment).Expand(workload, workloadFile), catalog, environment, hasRuntime: true);

    /// <summary>
    /// Resolves an environment definition's substrate requirements through the same engine. The definition is the owner
    /// (its name stands for the workload, its team is empty) and has no runtime, so workload-scope policies add nothing.
    /// </summary>
    public static ResolvedGraph ResolveSubstrate(EnvironmentDefinition definition, string definitionFile, Catalog catalog, EnvironmentDescriptor environment) =>
        Build(new Expander(catalog, environment).Expand(definition.Name, string.Empty, definition.Requires, definitionFile), catalog, environment, hasRuntime: false);

    private static ResolvedGraph Build(ExpansionResult expansion, Catalog catalog, EnvironmentDescriptor environment, bool hasRuntime) =>
        new GraphBuilder(environment).Build(new PolicyApplier(catalog, environment, hasRuntime).Apply(expansion));
}
