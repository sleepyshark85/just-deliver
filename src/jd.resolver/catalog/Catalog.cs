namespace jd.resolver.catalog;

/// <summary>
/// The in-memory form of a catalog directory: everything the resolver resolves workloads and environments against. Formats:
/// schemas/catalog/*.schema.json. Expressions (${...}) and config values are kept exactly as written; the expression evaluator interprets
/// them later. Source is the catalog-relative file a record came from. <see cref="Layers"/>: the layers in deploy order, from the schemas.
/// <see cref="Rollouts"/>: each runtime part's probe and release block, by the runtime's name and the part's selection key.
/// <see cref="GrantTargets"/>: grant template to the input naming the resource the grant is on (catalog/grants.yaml).
/// </summary>
public sealed record Catalog(
    string Version,
    IReadOnlyList<string> Layers,
    IReadOnlyDictionary<string, EnvironmentType> EnvironmentTypes,
    IReadOnlyDictionary<string, PartOwner> Components,
    IReadOnlyDictionary<string, PartOwner> SharedResources,
    IReadOnlyDictionary<string, PartOwner> Runtimes,
    IReadOnlyDictionary<(string Runtime, string Key), (Probe Probe, RuntimeRelease Release)> Rollouts,
    IReadOnlyDictionary<string, string> GrantTargets,
    IReadOnlyList<Policy> Policies,
    IReadOnlyDictionary<string, NamingRule> Naming,
    IReadOnlyDictionary<string, string> Roles)
{
    /// <summary>The key of <see cref="Rollouts"/> for <paramref name="part"/> of <paramref name="runtime"/>: the runtime's name and the part's one selection key.</summary>
    public static (string Runtime, string Key) RolloutKey(PartOwner runtime, Part part) => (runtime.Name, part.Keys.Single().Key);
}
