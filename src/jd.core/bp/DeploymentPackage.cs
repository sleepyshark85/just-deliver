namespace jd.core.bp;

/// <summary>Everything a backend needs to deploy or preview one graph node: its stack, its template and the template's inputs.</summary>
public sealed class DeploymentPackage
{
    /// <summary>The graph's stack for this node: one backend stack per node. The backend may shorten it to fit its limits.</summary>
    public required string StackName { get; set; }

    /// <summary>The template (Pulumi.yaml): its project name defines which project the stack belongs to.</summary>
    public required string DeploymentContent { get; set; }
    /// <summary>The template's inputs, by input name, as the node's config resolved them.</summary>
    public required Dictionary<string, ConfigEntry> DeploymentParameters { get; set; }
}
