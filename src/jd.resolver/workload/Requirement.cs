namespace jd.resolver.workload;

/// <summary>A requirement of a workload: the component it asks for and the class that selects its part.</summary>
/// <param name="Type">The component it asks for.</param>
/// <param name="Class">The class that selects the component's class part.</param>
public sealed record Requirement(string Type, string Class)
{
    /// <summary>The id it declares; null when it declares none.</summary>
    internal string? DeclaredId { get; init; }

    /// <summary>Where it was read, such as <c>requires[0]</c>.</summary>
    internal string Location { get; init; } = string.Empty;

    /// <summary>Its effective id: the id it declares, else its type (ADR 0011). Unique per workload (<see cref="WorkloadRules"/>).</summary>
    public string Id => DeclaredId ?? Type;
}
