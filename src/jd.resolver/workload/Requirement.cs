namespace jd.resolver.workload;

/// <summary>A requirement of a workload: the component it asks for and the class that selects its part.</summary>
/// <param name="Type">The component it asks for.</param>
/// <param name="DeclaredId">The id it declares; null when it declares none.</param>
/// <param name="Class">The class that selects the component's class part.</param>
/// <param name="Location">Where it was read, such as <c>requires[0]</c>.</param>
public sealed record Requirement(string Type, string? DeclaredId, string Class, string Location)
{
    /// <summary>Its effective id: the id it declares, else its type (ADR 0011). Unique per workload (<see cref="WorkloadRules"/>).</summary>
    public string Id => DeclaredId ?? Type;
}
