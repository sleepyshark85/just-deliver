namespace jd.core.bp;

/// <summary>One property of a resource that a deploy changes, with its value before and after, shown in a preview.</summary>
public sealed class PropertyChange
{
    /// <summary>The property's path in the resource's inputs as the backend reports it (<c>properties.sku.name</c>).</summary>
    public required string Path { get; set; }
    /// <summary>The value before the change; null when the property was not set.</summary>
    public string? OldValue { get; set; }
    /// <summary>The value after the change; null when the change removes the property.</summary>
    public string? NewValue { get; set; }
}
