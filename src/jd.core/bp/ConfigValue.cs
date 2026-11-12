namespace jd.core.bp;

/// <summary>One named value passed to or read back from a backend stack: a deployment parameter or a stack output.</summary>
public sealed class ConfigEntry
{
    /// <summary>
    /// Null when read back from a deployment output the stack exported as null -
    /// callers reading a value they require should fail rather than treat it as empty.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>True when the backend must store the value encrypted and never print it.</summary>
    public bool IsSecret { get; set; }

    /// <summary>A value, plain unless <paramref name="isSecret"/> says it must be kept secret.</summary>
    public ConfigEntry(
        string? value,
        bool isSecret = false)
    {
        this.Value = value;
        this.IsSecret = isSecret;
    }
}
