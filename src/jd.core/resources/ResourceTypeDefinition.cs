namespace jd.core.resources;

/// <summary>
/// A resource type contract, loaded from src/jd.core/types/&lt;name&gt;.yml. Backend-neutral:
/// what a workload may declare and what it gets back, never how a cloud delivers it.
///
/// Data rather than C# classes so a type can be added or tuned without a platform build.
/// The cost is that nothing is compiler-checked, so drift between a type and its backend
/// translation has to be caught by validation instead.
/// </summary>
public class ResourceTypeDefinition
{
    /// <summary>Discriminator a workload writes, e.g. `type: database`.</summary>
    public string Name { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>What a workload may set, keyed by property name.</summary>
    public Dictionary<string, PropertyDescriptor> Properties { get; set; } = new();

    /// <summary>
    /// What the resource exposes, keyed by the reference key used in
    /// ${resource.&lt;name&gt;.&lt;key&gt;}.
    /// </summary>
    public Dictionary<string, OutputDescriptor> Outputs { get; set; } = new();

    public IEnumerable<string> OverridableProperties =>
        Properties.Where(p => p.Value.Overridable).Select(p => p.Key);

    public IEnumerable<string> RequiredProperties =>
        Properties.Where(p => p.Value.Required).Select(p => p.Key);

    public IEnumerable<string> SecretOutputs =>
        Outputs.Where(o => o.Value.Secret).Select(o => o.Key);
}

public class PropertyDescriptor
{
    /// <summary>string, enum, integer, boolean, map or list.</summary>
    public string Type { get; set; } = "string";

    /// <summary>Permitted values when <see cref="Type"/> is enum; empty otherwise.</summary>
    public List<string> Values { get; set; } = new();

    public string? Default { get; set; }

    public bool Required { get; set; }

    /// <summary>
    /// Whether a team may override this via the escape hatch. Declared on the property so
    /// the whitelist cannot drift from the type, unlike a separate whitelist document.
    /// </summary>
    public bool Overridable { get; set; }

    public string Description { get; set; } = "";
}

public class OutputDescriptor
{
    public string Type { get; set; } = "string";

    /// <summary>
    /// True when the value is credential material. Secret outputs must not be written into
    /// plain app settings or logs - they belong in a vault, with a reference handed over.
    /// </summary>
    public bool Secret { get; set; }

    public string Description { get; set; } = "";
}
