using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>How the workloads of a release walk ended.</summary>
/// <param name="Deployed">The workloads that succeeded and changed a node.</param>
/// <param name="Unchanged">The workloads that succeeded and changed nothing.</param>
/// <param name="Previewed">The workloads that succeeded in a preview.</param>
/// <param name="Failed">The workloads that failed: the walk stops at the first.</param>
/// <param name="NotStarted">The workloads after a failure, in deploy order.</param>
public sealed record ReleaseSummary(int Deployed, int Unchanged, int Previewed, int Failed, IReadOnlyList<string> NotStarted)
{
    // A workload that succeeded is deployed when a node changed and previewed when its nodes were only previewed (or wait on an upstream node, in a
    // preview); otherwise it is unchanged.
    internal static ReleaseSummary Of(ReleaseSet set, IReadOnlyList<WorkloadRun> runs)
    {
        var succeeded = runs.Where(r => r.Run.Succeeded).ToList();
        var deployed = succeeded.Count(r => r.Run.Nodes.Any(n => n.Outcome == NodeOutcome.Deployed));
        var previewed = succeeded.Count(r => r.Run.Nodes.All(n => n.Outcome is NodeOutcome.Previewed or NodeOutcome.PendingUpstream));
        return new(deployed, succeeded.Count - deployed - previewed, previewed, runs.Count - succeeded.Count, [.. set.Order.Skip(runs.Count)]);
    }
}
