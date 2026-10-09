namespace jd.core.bp;

public class DeploymentPackage
{
    /// <summary>The graph's stack for this node: one backend stack per node. The backend may shorten it to fit its limits.</summary>
    public required string StackName { get; set; }

    /// <summary>The template (Pulumi.yaml): its project name defines which project the stack belongs to.</summary>
    public required string DeploymentContent { get; set; }
    public required Dictionary<string, ConfigEntry> DeploymentParameters { get; set; }
}
