using jd.core.bp;
using jd.resolver.graph;

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

    Failed,
}

/// <summary>The steps of the runtime node's release flow, in order.</summary>
public enum Step
{
    /// <summary>Deploy the new revision with no traffic (the live revision keeps all of it).</summary>
    Dark,

    /// <summary>Deploy the nodes that depend on the runtime (the grants).</summary>
    Grants,

    /// <summary>Check the new revision is healthy.</summary>
    Probe,

    /// <summary>Send all traffic to the new revision.</summary>
    Shift,
}

public enum StepOutcome
{
    Done,

    /// <summary>Preview only: the step would run.</summary>
    Planned,

    /// <summary>Nothing to do: the revision is unchanged.</summary>
    Skipped,

    Failed,
}

/// <param name="Step">Which step.</param>
/// <param name="Outcome">What happened to it.</param>
/// <param name="Detail">The new revision, the probe's last answer or the failure reason; empty when there is nothing to add.</param>
public sealed record StepReport(Step Step, StepOutcome Outcome, string Detail);

/// <param name="NodeId">The graph node's id.</param>
/// <param name="Outcome">What happened to the node.</param>
/// <param name="Summary">The provider's count of resources per operation; empty when nothing was run.</param>
/// <param name="Changes">The provider's per-resource changes; empty when nothing was run.</param>
/// <param name="Message">Why a node is pending or failed; empty otherwise. A failed release step reads <c>&lt;step&gt;: &lt;reason&gt;</c>.</param>
/// <param name="Elapsed">How long the node took.</param>
/// <param name="Outputs">The non-secret outputs the stack exported after a deploy; empty otherwise. Secrets stay inside the walk: reports get persisted.</param>
/// <param name="Steps">The runtime node's release steps that ran, in order (a failed step is the last); null for every other node.</param>
public sealed record NodeReport(
    string NodeId,
    NodeOutcome Outcome,
    IReadOnlyDictionary<string, int> Summary,
    IReadOnlyList<ResourceChange> Changes,
    string Message,
    TimeSpan Elapsed,
    IReadOnlyDictionary<string, ConfigEntry> Outputs,
    IReadOnlyList<StepReport>? Steps = null)
{
    internal static NodeReport For(GraphNode node, NodeOutcome outcome, string message, DeploymentResult? result = null, IReadOnlyList<StepReport>? steps = null) =>
        new(node.Id, outcome, result?.Summary ?? [], result?.Changes ?? [], message, TimeSpan.Zero,
            result?.Outputs.Where(o => !o.Value.IsSecret).ToDictionary(o => o.Key, o => o.Value) ?? [], steps);
}

/// <summary>The nodes handled, in the order they ran. A failure ends the walk, so nodes after it are absent.</summary>
public sealed record RunReport(IReadOnlyList<NodeReport> Nodes)
{
    public bool Succeeded => Nodes.All(n => n.Outcome != NodeOutcome.Failed);
}

/// <summary>One workload's part of a release run.</summary>
public sealed record WorkloadRun(string Workload, RunReport Run);
