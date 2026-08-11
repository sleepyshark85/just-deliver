using YamlDotNet.Serialization;

namespace jd.resolver.Models;

/// <summary>
/// A submitted workload definition. Shape is enforced by schemas/workload.schema.json -
/// this type assumes the document has already been validated against it.
/// </summary>
public class WorkloadDocument
{
    public string ApiVersion { get; set; } = "";
    public string Kind { get; set; } = "";
    public WorkloadMetadata Metadata { get; set; } = new();
    public WorkloadContainer Container { get; set; } = new();
    public List<WorkloadRequirement> Requires { get; set; } = new();
}

public class WorkloadMetadata
{
    public string Name { get; set; } = "";
    public string Team { get; set; } = "";
    public string Environment { get; set; } = "";
}

public class WorkloadContainer
{
    public string Image { get; set; } = "";
    public Dictionary<string, string> Variables { get; set; } = new();
    public List<WorkloadPort> Ports { get; set; } = new();
}

public class WorkloadPort
{
    public int Port { get; set; }
    public string Protocol { get; set; } = "TCP";
}

public class WorkloadRequirement
{
    public string Type { get; set; } = "";

    public Dictionary<string, string> Overrides { get; set; } = new();

    // The one snake_case key in an otherwise camelCase document.
    [YamlMember(Alias = "override_reason")]
    public string? OverrideReason { get; set; }
}
