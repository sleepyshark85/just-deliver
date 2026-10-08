namespace jd.core.bp;

public class DeploymentPackage
{
    public required string Name { get; set; }
    public required string Version { get; set; }

    public required string DeploymentContent { get; set; }
    public string? DeploymentDefaultParametersContent { get; set; }
    public required Dictionary<string, ConfigEntry> DeploymentParameters { get; set; }
}
