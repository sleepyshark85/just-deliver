public class ConfigEntry
{
    public string Value { get; set; }

    public bool IsSecret { get; set; }

    public ConfigEntry(
        string value,
        bool isSecret = false)
    {
        this.Value = value;
        this.IsSecret = isSecret;
    }
}