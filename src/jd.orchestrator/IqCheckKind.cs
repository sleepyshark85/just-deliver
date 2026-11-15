namespace jd.orchestrator;

/// <summary>What an installation-qualification check compares with the live deployment.</summary>
public enum IqCheckKind
{
    /// <summary>A node of the workload's graph, previewed against what is deployed: any change means the deployment drifted.</summary>
    Drift,

    /// <summary>A revision that holds traffic runs the pinned image.</summary>
    Image,
}
