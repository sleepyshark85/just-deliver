namespace jd.core.resources;

/// <summary>
/// The identity a workload acts as when it reaches other resources.
///
/// Deliberately not an output of <see cref="Computing"/>, even though on Azure it is
/// materialised by the runtime's system-assigned identity. Every resource that grants access
/// - a database, a queue, a vault - needs it, and making it a property of the workload rather
/// than of one runtime keeps those resources from reaching into another resource type's
/// outputs. That would otherwise be the messiest edge in the dependency graph.
/// </summary>
public class WorkloadIdentity
{
    /// <summary>
    /// Object id of the principal. Null until the runtime that materialises it has been
    /// provisioned, so anything granting access to it necessarily orders after that.
    /// </summary>
    public string? PrincipalId { get; set; }
}
