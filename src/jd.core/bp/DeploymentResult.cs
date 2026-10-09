namespace jd.core.bp;

public class DeploymentResult
{
    public required Dictionary<string, ConfigEntry> Outputs { get; set; }

    /// <summary>Count of resources per operation (e.g. "Create": 2, "Update": 1, "Same": 3), from the update summary.</summary>
    public Dictionary<string, int> Summary { get; set; } = new();

    private const string NoChangeOperation = "Same";

    /// <summary>True for every operation except "Same": any other operation (create, update, delete, replace, ...) counts as a change.</summary>
    public static bool IsChange(string operation) => !string.Equals(operation, NoChangeOperation, StringComparison.OrdinalIgnoreCase);

    /// <summary>The operations of <paramref name="summary"/> that changed at least one resource, by <see cref="IsChange"/>.</summary>
    public static IEnumerable<KeyValuePair<string, int>> ChangedOperations(IReadOnlyDictionary<string, int> summary) =>
        summary.Where(s => s.Value > 0 && IsChange(s.Key));

    /// <summary>True when any resource would be, or was, changed.</summary>
    public bool HasChanges => ChangedOperations(Summary).Any();

    /// <summary>Per-resource changes with the specific properties that changed, from engine events.</summary>
    public List<ResourceChange> Changes { get; set; } = new();
}
