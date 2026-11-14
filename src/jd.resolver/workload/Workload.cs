namespace jd.resolver.workload;

/// <summary>
/// A workload (docs/architecture/documents.md section 3), bound from its definition by the <c>workload.*</c> and <c>requirement.*</c> roles: what
/// the expressions read as <c>${workload.…}</c>, the variables the runtime node deploys, and the requirements, in the order written.
/// </summary>
/// <param name="Name">The workload's name.</param>
/// <param name="Team">The team that owns it.</param>
/// <param name="Image">Its image.</param>
/// <param name="Port">Its first port; null when it declares none.</param>
/// <param name="Variables">Its variables, by name; workload input into the platform (boundary 6).</param>
/// <param name="Requirements">Its requirements, in the order written.</param>
public sealed record Workload(
    string Name,
    string Team,
    string Image,
    int? Port,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<Requirement> Requirements)
{
    /// <summary>Where the image was read, for an error about it.</summary>
    internal string ImageLocation { get; init; } = string.Empty;

    /// <summary>Where the variables were read; a variable is located at this location and its name.</summary>
    internal string VariablesLocation { get; init; } = string.Empty;
}
