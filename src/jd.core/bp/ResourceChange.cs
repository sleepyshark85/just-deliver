namespace jd.core.bp;

/// <summary>One resource a deploy creates, changes or deletes, as the deploy and preview output list it.</summary>
public sealed class ResourceChange
{
    /// <summary>The backend's unique name of the resource within its stack.</summary>
    public required string Urn { get; set; }
    /// <summary>The resource's type as the backend names it (<c>azure-native:storage:StorageAccount</c>).</summary>
    public required string Type { get; set; }
    /// <summary>What happens to the resource (<c>Create</c>, <c>Update</c>, <c>Delete</c>, <c>Replace</c>, ...); see <see cref="DeploymentResult.IsChange"/>.</summary>
    public required string Operation { get; set; }
    /// <summary>The properties the operation changes; empty when the backend reports no detail.</summary>
    public List<PropertyChange> ChangedProperties { get; set; } = new();
}
