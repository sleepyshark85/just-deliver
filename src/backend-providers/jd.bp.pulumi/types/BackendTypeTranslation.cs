using System.Text.RegularExpressions;

namespace jd.bp.pulumi.types;

/// <summary>
/// How one abstract resource type is realised as Pulumi deployments, loaded from
/// types/&lt;name&gt;.yml. The Pulumi-specific half of the two-layer model: core says what a
/// type promises, this says which definitions deliver it and with what parameters.
/// </summary>
public class BackendTypeTranslation
{
    /// <summary>Must name a resource type declared in jd.core/types.</summary>
    public string Type { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    public List<BackendDeployment> Deployments { get; set; } = new();

    /// <summary>
    /// Abstract output key -> the expression producing it. Keys must be a subset of the
    /// outputs the core type declares; anything a workload can reference and this does not
    /// produce resolves to null at runtime.
    /// </summary>
    public Dictionary<string, string> Outputs { get; set; } = new();

    /// <summary>
    /// Core property or output names this backend deliberately does not map, each with the
    /// reason. Declared rather than omitted so a gap is visible and countable rather than
    /// looking like an oversight - and so the coherence check can insist that every core
    /// property is either mapped or explicitly waived.
    /// </summary>
    public Dictionary<string, string> Unmapped { get; set; } = new();

    /// <summary>Every core property name this translation binds, across all deployments.</summary>
    public IEnumerable<string> MappedProperties =>
        Deployments.SelectMany(d => d.MappedProperties).Distinct();
}

public class BackendDeployment
{
    /// <summary>Unique across the whole types folder, not just this file.</summary>
    public string Id { get; set; } = "";

    /// <summary>Folder name under the shared settings' definitions_root.</summary>
    public string Definition { get; set; } = "";

    /// <summary>
    /// One parameter per entry: Pulumi parameter name -> value template. A template may
    /// reference a core property with ${property.x}, which is how a property maps 1-1 onto a
    /// parameter.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>
    /// A set of parameters per entry: core property name -> permitted value -> parameters.
    /// This is how a property maps 1-n, where one value of one property decides several
    /// parameters at once. Every value the core type permits must appear.
    ///
    /// Separate from <see cref="Parameters"/> because the shapes differ, not the purpose -
    /// both produce parameters. Merging them would put parameter names and property
    /// selectors as siblings in one map, meaning different things at the same level.
    /// </summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> ParameterSets { get; set; } = new();

    /// <summary>Every parameter name this deployment can set, from either block.</summary>
    public IEnumerable<string> AllParameterNames => Parameters.Keys
        .Concat(ParameterSets.Values.SelectMany(v => v.Values).SelectMany(p => p.Keys))
        .Distinct();

    /// <summary>
    /// Core properties this deployment binds: those it keys a parameter set on, plus any
    /// referenced by a ${property.x} expression in a single-parameter template.
    /// </summary>
    public IEnumerable<string> MappedProperties => ParameterSets.Keys
        .Concat(Parameters.Values.SelectMany(PropertyReferences))
        .Distinct();

    private static readonly Regex PropertyExpression = new(@"\$\{property\.([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

    public static IEnumerable<string> PropertyReferences(string value) =>
        PropertyExpression.Matches(value).Select(m => m.Groups[1].Value);
}

/// <summary>types/_shared.yml</summary>
public class BackendSharedSettings
{
    public string DefinitionsRoot { get; set; } = "";

    public Dictionary<string, Dictionary<string, string>> Environments { get; set; } = new();

    public Dictionary<string, string> Roles { get; set; } = new();

    /// <summary>
    /// Resource types added to every workload whether or not it declares them. Platform
    /// policy rather than a backend concern - here only because no policy layer exists yet.
    /// </summary>
    public List<PolicyAttachedResource> PolicyAttached { get; set; } = new();

    /// <summary>
    /// Deployed once per workload before any type translation. Azure-specific, which is why
    /// it lives here rather than as a core resource type.
    /// </summary>
    public List<BackendDeployment> Foundation { get; set; } = new();
}

public class PolicyAttachedResource
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "";
}
