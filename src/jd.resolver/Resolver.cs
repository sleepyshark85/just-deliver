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
        Build(new Expander(catalog, environment).Expand(workload, workloadFile), catalog, environment);

    /// <summary>
    /// Resolves what an environment definition's offers reach in its type's layer through the same engine. The definition is the owner:
    /// its name, prefixed with <c>@</c> (so no workload can share ids, stacks or <c>name()</c> hashes), stands for the workload, its team is
    /// empty, and it has no runtime, so workload-scope policies do not apply. <paramref name="baseDescriptor"/> holds the parts deployed before.
    /// </summary>
    public static ResolvedGraph ResolveSubstrate(
        EnvironmentDefinition definition, string definitionFile, Catalog catalog, EnvironmentDescriptor environment, EnvironmentDescriptor? baseDescriptor) =>
        Build(new Expander(catalog, environment).ExpandEnvironment(definition, definitionFile, baseDescriptor), catalog, environment);

    private static ResolvedGraph Build(ExpansionResult expansion, Catalog catalog, EnvironmentDescriptor environment) =>
        new GraphBuilder(environment, catalog.GrantTargets).Build(new PolicyApplier(catalog, environment).Apply(expansion));
}
