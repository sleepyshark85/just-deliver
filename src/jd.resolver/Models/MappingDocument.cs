namespace jd.resolver.Models;

/// <summary>
/// One definitions/mappings/&lt;resource-type&gt;.yml file: how a resource type is
/// realised as one or more Pulumi deployments.
/// </summary>
public class MappingDocument
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    public string ResourceType { get; set; } = "";

    public List<MappingDeployment> Deployments { get; set; } = new();

    /// <summary>
    /// What ${resources.&lt;type&gt;.&lt;key&gt;} resolves to. Values are expressions,
    /// normally pointing at a deployment output.
    /// </summary>
    public Dictionary<string, string> Outputs { get; set; } = new();

    public List<string> OverrideWhitelist { get; set; } = new();
}

public class MappingDeployment
{
    /// <summary>Unique across the whole mappings folder, not just this file.</summary>
    public string Id { get; set; } = "";

    /// <summary>Folder name under the shared settings' definitions_root.</summary>
    public string Definition { get; set; } = "";

    /// <summary>
    /// True on the single deployment that runs the workload's container. Its
    /// container.variables determine extra dependency edges, since a variable
    /// referencing ${resources.database.endpoint} cannot be set before the database exists.
    /// </summary>
    public bool HostsContainer { get; set; }

    /// <summary>
    /// Parameter bindings. Kept as raw scalar text so YAML booleans and numbers
    /// reach Pulumi config as "true"/"31" rather than .NET's "True".
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>Per-environment overlay applied on top of <see cref="Parameters"/>.</summary>
    public Dictionary<string, Dictionary<string, string>> Environments { get; set; } = new();
}

/// <summary>definitions/mappings/_shared.yml</summary>
public class SharedSettings
{
    public string DefinitionsRoot { get; set; } = "";

    public Dictionary<string, Dictionary<string, string>> Environments { get; set; } = new();

    /// <summary>Built-in role alias -> role definition GUID.</summary>
    public Dictionary<string, string> Roles { get; set; } = new();

    /// <summary>Resource types provisioned for every workload, requested or not.</summary>
    public List<string> PolicyAttached { get; set; } = new();
}
