namespace jd.resolver.environment;

/// <summary>
/// An environment (docs/architecture/documents.md section 3): one record for an environment definition, which <c>jd env up</c> provisions, and
/// for the descriptor it publishes to the workloads deployed on it, bound from either by the <c>env.*</c> roles. <see cref="Offers"/> names the
/// components and the runtime a workload may get there. <see cref="Values"/> are the deployed parts' exports, keyed by the flattened dot path
/// <c>&lt;part&gt;.&lt;export&gt;</c>, read as <c>${&lt;part&gt;.&lt;export&gt;}</c>; a definition has none of its own. An environment with an
/// <see cref="Approval"/> policy is gated: it takes deploys only through <c>jd promote</c> (docs/architecture/approvals.md section 2).
/// Named for the descriptor, not <c>Environment</c>, which would clash with <see cref="System.Environment"/> wherever both are in scope.
/// </summary>
public sealed record EnvironmentDescriptor(
    string Name,
    string Region,
    string Type,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> Offers,
    ApprovalPolicy? Approval)
{
    /// <summary>
    /// Where the binder read each role of the document's kind, by role (<see cref="Roles"/>); a role the document leaves out is located by its
    /// property name, and a nested role (one in the approval block, which is absent) by its own leaf name. Read through <see cref="Location"/>.
    /// </summary>
    internal IReadOnlyDictionary<string, string> Locations { get; init; } = new Dictionary<string, string>();

    /// <summary>Where <paramref name="role"/> was read (<see cref="Locations"/>); empty when the environment was not bound from a document.</summary>
    internal string Location(string role) => Locations.GetValueOrDefault(role, string.Empty);

    /// <summary>The environment a definition is resolved in: the definition's own name, region, type, offers and approval policy, and the base descriptor's values.</summary>
    public EnvironmentDescriptor Over(EnvironmentDescriptor? baseDescriptor) => this with { Values = baseDescriptor?.Values ?? new Dictionary<string, string>() };
}
