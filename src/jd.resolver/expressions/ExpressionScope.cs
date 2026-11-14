namespace jd.resolver.expressions;

/// <summary>The scope an expression is evaluated in: what it names as its current id, and the nodes, outputs and parts it reads.</summary>
/// <param name="CurrentId">The requirement's effective id, or the node name for nodes not tied to a requirement.</param>
/// <param name="NodeNames">Nodes in scope for <c>${node.output}</c>.</param>
/// <param name="KnownOutputs">Outputs already known; a reference found here resolves instead of staying pending.</param>
/// <param name="Parts">The exports (name to expression) of the parts deployed in this run, by part name: <c>${&lt;name&gt;.&lt;export&gt;}</c> of one of
/// them is evaluated in place. Empty in a workload run.</param>
public sealed record ExpressionScope(
    string CurrentId,
    IReadOnlySet<string> NodeNames,
    IReadOnlyDictionary<Reference, string> KnownOutputs,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parts);
