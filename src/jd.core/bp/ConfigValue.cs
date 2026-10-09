namespace jd.core.bp;

public class ConfigEntry
{
    /// <summary>
    /// Null when read back from a deployment output the stack exported as null -
    /// callers reading a value they require should fail rather than treat it as empty.
    /// </summary>
    public string? Value { get; set; }

    public bool IsSecret { get; set; }

    public ConfigEntry(
        string? value,
        bool isSecret = false)
    {
        this.Value = value;
        this.IsSecret = isSecret;
    }
}
