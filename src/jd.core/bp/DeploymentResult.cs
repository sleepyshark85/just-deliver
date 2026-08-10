namespace jd.core.bp;

public class DeploymentResult
{
    public Dictionary<string, ConfigEntry> Outputs { get; set; }

    /// <summary>Count of resources per operation (e.g. "create": 2, "update": 1), from the update summary.</summary>
    public Dictionary<string, int> Summary { get; set; } = new();

    /// <summary>Per-resource changes with the specific properties that changed, from engine events.</summary>
    public List<ResourceChange> Changes { get; set; } = new();
}
