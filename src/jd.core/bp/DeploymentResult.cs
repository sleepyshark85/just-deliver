namespace jd.core.bp;

public class DeploymentResult
{
    public required Dictionary<string, ConfigEntry> Outputs { get; set; }

    /// <summary>Count of resources per operation (e.g. "Create": 2, "Update": 1, "Same": 3), from the update summary.</summary>
    public Dictionary<string, int> Summary { get; set; } = new();

    /// <summary>The operation that leaves a resource as it is; every other operation in <see cref="Summary"/> changes something.</summary>
    public const string NoChangeOperation = "Same";

    /// <summary>True when any resource would be, or was, created, updated, deleted or replaced.</summary>
    public bool HasChanges => Summary.Any(s => s.Value > 0 && !string.Equals(s.Key, NoChangeOperation, StringComparison.OrdinalIgnoreCase));

    /// <summary>Per-resource changes with the specific properties that changed, from engine events.</summary>
    public List<ResourceChange> Changes { get; set; } = new();
}
