namespace jd.core.bp;

public class PropertyChange
{
    public required string Path { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
