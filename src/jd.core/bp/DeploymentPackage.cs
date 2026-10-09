namespace jd.core.bp;

public class DeploymentPackage
{
    /// <summary>The template name: the backend project that defines the resources.</summary>
    public required string Name { get; set; }

    /// <summary>The graph's stack for this node: one backend stack per node. The backend may shorten it to fit its limits.</summary>
    public required string StackName { get; set; }
    public required string Version { get; set; }

    public required string DeploymentContent { get; set; }
    public string? DeploymentDefaultParametersContent { get; set; }
    public required Dictionary<string, ConfigEntry> DeploymentParameters { get; set; }
}
