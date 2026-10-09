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

    /// <summary>Depends on the runtime directly or through another node.</summary>
    AfterRuntime,
}

// Id is <workload>/<env>/<scope>/<node>; scope is the requirement's effective id, or GraphBuilder.WorkloadScope.
// Stack is the id with '/' replaced by '-': one backend stack per node.
// Hash is lowercase hex SHA-256 of the node's template, kind and canonical config (ConfigJson).
// DependsOn holds the ids of the nodes whose outputs this node references, sorted. DependsOnRuntime means it references
// runtime.*; the runtime node itself arrives with the runtime mapping.
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
    bool DependsOnRuntime);

/// <summary>
/// The resolver's output. <see cref="Nodes"/> are in topological order (dependencies first, ties broken by id);
/// <see cref="Exports"/> are each requirement's exports by requirement id. Usable only when <see cref="Errors"/> is empty.
/// </summary>
public sealed record ResolvedGraph(
    string CatalogVersion,
    string Environment,
    string Workload,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, EvalResult>> Exports,
    IReadOnlyList<LoadError> Errors);
