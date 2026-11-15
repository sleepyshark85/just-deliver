namespace jd.resolver.release;

/// <summary>A release manifest or a release set as the binder reads it, by the <c>release.*</c> roles (docs/architecture/documents.md section 5).</summary>
/// <param name="Label">The release label (<see cref="Roles.ReleaseLabel"/>).</param>
/// <param name="CreatedAt">When a set was created, as written (<see cref="Roles.ReleaseCreatedAt"/>); empty for a manifest.</param>
/// <param name="Workloads">One entry per workload (<see cref="Roles.ReleaseWorkloads"/>), in the document's order.</param>
internal sealed record ReleaseRead(string Label, string CreatedAt, IReadOnlyList<ReleaseEntry> Workloads)
{
    /// <summary>Where <see cref="CreatedAt"/> is held.</summary>
    public required string CreatedAtLocation { get; init; }

    /// <summary>Where the list of <see cref="Workloads"/> is held.</summary>
    public required string WorkloadsLocation { get; init; }
}
