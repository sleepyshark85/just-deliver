using jd.core.bp;

namespace jd.orchestrator;

public enum NodeOutcome
{
    /// <summary>Deployed, and the provider reported changes.</summary>
    Deployed,

    /// <summary>Deployed, and the provider reported no changes: the stack already matched.</summary>
    Unchanged,

    /// <summary>Previewed: the changes are in the report; nothing was created or modified.</summary>
    Previewed,

    /// <summary>Preview only: a value from an upstream node that is not deployed is missing, so the node cannot be previewed.</summary>
    PendingUpstream,

    /// <summary>Depends on the runtime; it is deployed once the runtime exists, not by this walk.</summary>
    WaitingForRuntime,

    Failed,
}

/// <param name="NodeId">The graph node's id.</param>
/// <param name="Outcome">What happened to the node.</param>
/// <param name="Summary">The provider's count of resources per operation; empty when nothing was run.</param>
/// <param name="Changes">The provider's per-resource changes; empty when nothing was run.</param>
/// <param name="Message">Why a node is pending, waiting or failed; empty otherwise.</param>
/// <param name="Elapsed">How long the node took.</param>
/// <param name="Outputs">The outputs the stack exported after a deploy (secrets flagged); empty otherwise.</param>
public sealed record NodeReport(
    string NodeId,
    NodeOutcome Outcome,
    IReadOnlyDictionary<string, int> Summary,
    IReadOnlyList<ResourceChange> Changes,
    string Message,
    TimeSpan Elapsed,
    IReadOnlyDictionary<string, ConfigEntry> Outputs);

/// <summary>The nodes handled, in graph order. A failure ends the walk, so nodes after it are absent.</summary>
public sealed record RunReport(IReadOnlyList<NodeReport> Nodes)
{
    public bool Succeeded => Nodes.All(n => n.Outcome != NodeOutcome.Failed);
}
