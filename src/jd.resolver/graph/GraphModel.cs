using jd.resolver.catalog;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.policies;

namespace jd.resolver.graph;

/// <summary>Derived from the dependencies, never listed per template: whether a node waits for the runtime.</summary>
public enum Phase
{
    /// <summary>No transitive dependency on the runtime.</summary>
    Infrastructure,

    /// <summary>The runtime node itself; deployed by its own step, not by the infrastructure walk.</summary>
    Runtime,

    /// <summary>Depends on the runtime directly or through another node.</summary>
    AfterRuntime,
}

// Id is <workload>/<env>/<scope>/<node>; scope is the requirement's effective id, or GraphBuilder.WorkloadScope.
// Stack is the id with '/' replaced by '.' and '@' by '_' (Pulumi stack names allow [A-Za-z0-9_.-]): one backend stack
// per node, unique because ids contain neither '.' nor '_'.
// Hash is lowercase hex SHA-256 of the node's template, kind and canonical config (ConfigJson).
// DependsOn holds the ids of the nodes whose outputs this node references, sorted; a runtime.* reference is an edge to the
// runtime node (scope GraphBuilder.WorkloadScope, name ExpressionEvaluator.RuntimeNode). Probe is set on the runtime node only.
public sealed record GraphNode(
    string Id,
    string Scope,
    string Name,
    string Template,
    NodeKind Kind,
    string Stack,
    Phase Phase,
    IReadOnlyDictionary<string, ConfigValue> Config,
    IReadOnlyDictionary<string, Provenance> Provenance,
    string Hash,
    IReadOnlyList<string> DependsOn,
    Probe? Probe = null);

/// <summary>
/// The resolver's output. <see cref="Nodes"/> are in topological order (dependencies first, ties broken by id);
/// <see cref="Exports"/> are each requirement's exports by requirement id. Usable only when <see cref="Errors"/> is empty.
/// </summary>
public sealed record ResolvedGraph(
    string CatalogVersion,
    string Environment,
    string Workload,
    string WorkloadTeam,
    string? WorkloadImage,
    int? WorkloadPort,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, EvalResult>> Exports,
    IReadOnlyList<LoadError> Errors);
