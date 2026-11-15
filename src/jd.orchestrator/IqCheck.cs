namespace jd.orchestrator;

/// <param name="Kind">What was checked.</param>
/// <param name="Workload">The workload it belongs to.</param>
/// <param name="Subject">The node (drift), the revision (image) or, when there is none to name, the workload.</param>
/// <param name="Failure">Why it failed, naming the node or revision; empty when it passed.</param>
public sealed record IqCheck(IqCheckKind Kind, string Workload, string Subject, string Failure)
{
    /// <summary>True when the check found the deployment as the release pins it.</summary>
    public bool Passed => Failure.Length == 0;
}
