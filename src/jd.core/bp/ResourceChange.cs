namespace jd.core.bp;

public class ResourceChange
{
    public required string Urn { get; set; }
    public required string Type { get; set; }
    public required string Operation { get; set; }
    public List<PropertyChange> ChangedProperties { get; set; } = new();
}
