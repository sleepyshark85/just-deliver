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
    /// Where each role of the document's kind was read, by role (<see cref="Roles"/>); a field the document leaves out is located by its name.
    /// Empty for an environment not read from a document, which no rule that locates an error is given.
    /// </summary>
    internal IReadOnlyDictionary<string, string> Locations { get; init; } = new Dictionary<string, string>();

    /// <summary>The environment a definition is resolved in: the definition's own name, region, type, offers and approval policy, and the base descriptor's values.</summary>
    public EnvironmentDescriptor Over(EnvironmentDescriptor? baseDescriptor) => this with { Values = baseDescriptor?.Values ?? new Dictionary<string, string>() };
}
