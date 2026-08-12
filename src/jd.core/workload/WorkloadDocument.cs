namespace jd.core.workload;

/// <summary>
/// A submitted workload definition. Backend-neutral: it names resource types and their
/// intent, never a cloud service.
/// </summary>
public class WorkloadDocument
{
    public string ApiVersion { get; set; } = "";

    public string Kind { get; set; } = "";

    public WorkloadMetadata Metadata { get; set; } = new();

    /// <summary>
    /// Everything the workload needs, including its own runtime. A uniform list rather than a
    /// special `container:` block, so a runtime is just another named resource - which is what
    /// lets a workload declare two of them, and what gives every resource a name to be
    /// referenced by.
    /// </summary>
    public List<WorkloadResource> Resources { get; set; } = new();

    public WorkloadResource? Resource(string name) =>
        Resources.FirstOrDefault(r => r.Name == name);
}

public class WorkloadMetadata
{
    public string Name { get; set; } = "";

    public string Team { get; set; } = "";

    public string Environment { get; set; } = "";
}

public class WorkloadResource
{
    /// <summary>Unique within the workload. What ${resource.&lt;name&gt;.&lt;output&gt;} keys on.</summary>
    public string Name { get; set; } = "";

    /// <summary>Must name a type in the core catalog.</summary>
    public string Type { get; set; } = "";

    /// <summary>
    /// Everything else the workload wrote, validated against the type contract rather than a
    /// fixed shape - the properties a resource accepts depend on its type. Scalars arrive as
    /// strings; maps and lists keep their structure.
    /// </summary>
    public Dictionary<string, object?> Properties { get; set; } = new();

    /// <summary>True when the platform added this rather than the team declaring it.</summary>
    public bool PolicyAttached { get; set; }

    public string? Scalar(string property) =>
        Properties.TryGetValue(property, out var value) ? value as string : null;

    /// <summary>A map-valued property, e.g. computing's environment variables.</summary>
    public IReadOnlyDictionary<string, string> Map(string property)
    {
        if (!Properties.TryGetValue(property, out var value) || value is null)
        {
            return new Dictionary<string, string>();
        }

        return value switch
        {
            IDictionary<string, object?> typed =>
                typed.ToDictionary(e => e.Key, e => e.Value?.ToString() ?? ""),
            System.Collections.IDictionary raw =>
                raw.Keys.Cast<object>().ToDictionary(k => k.ToString()!, k => raw[k]?.ToString() ?? ""),
            _ => new Dictionary<string, string>(),
        };
    }
}
