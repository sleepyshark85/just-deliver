namespace jd.core.bp;

public class ResourceChange
{
    public string Urn { get; set; }
    public string Type { get; set; }
    public string Operation { get; set; }
    public List<PropertyChange> ChangedProperties { get; set; } = new();
}
