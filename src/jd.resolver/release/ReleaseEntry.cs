namespace jd.resolver.release;

/// <summary>
/// One workload entry of a release manifest or a release set, bound by the <c>release.*</c> roles (docs/architecture/documents.md section 5): a
/// manifest names where the definition is, a set embeds it with the hash that pins it. A role the entry's kind does not bind reads empty.
/// </summary>
/// <param name="DefinitionPath">Where the definition is, relative to the manifest (<see cref="Roles.ReleaseDefinitionPath"/>).</param>
/// <param name="Definition">The definition as received (<see cref="Roles.ReleaseDefinition"/>).</param>
/// <param name="DefinitionSha256">The hash recorded for the definition (<see cref="Roles.ReleaseDefinitionSha256"/>).</param>
/// <param name="DependsOn">Names of workloads that deploy first (<see cref="Roles.ReleaseDependsOn"/>).</param>
internal sealed record ReleaseEntry(string DefinitionPath, string Definition, string DefinitionSha256, IReadOnlyList<string> DependsOn)
{
    /// <summary>Where the entry is held, as messages name it.</summary>
    public required string Location { get; init; }

    /// <summary>Where the entry's definition is held, its path in a manifest or its text in a set.</summary>
    public required string DefinitionLocation { get; init; }

    /// <summary>Where each of <see cref="DependsOn"/> is held, in the same order.</summary>
    public required IReadOnlyList<string> DependsOnLocations { get; init; }
}
