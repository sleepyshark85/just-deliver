namespace jd.resolver.environment;

/// <summary>
/// What <c>jd env up</c> provisions in <see cref="Region"/>: what the <see cref="Offers"/> (component and runtime names) reach in its type's layer.
/// Format: schemas/environment-definition.schema.json.
/// </summary>
public sealed record EnvironmentDefinition(string Name, string Region, string Type, IReadOnlyList<string> Offers, ApprovalPolicy? Approval)
{
    /// <summary>The environment the definition is resolved in: its own name, region, type, offers and approval policy, and the base descriptor's values.</summary>
    public EnvironmentDescriptor Over(EnvironmentDescriptor? baseDescriptor) =>
        new(Name, Region, Type, baseDescriptor?.Values ?? new Dictionary<string, string>(), Offers, Approval);
}

/// <summary>Reads an environment definition and validates it against its schema; the catalog's rules are checked when it is resolved.</summary>
public static class EnvironmentDefinitionFile
{
    // Bridge: the kind read here until the environment is bound from its schema (S24c1).
    private const string Kind = "EnvironmentDefinition";

    /// <summary>Loads the definition at <paramref name="path"/>; the value is set exactly when there are no errors.</summary>
    public static async Task<LoadResult<EnvironmentDefinition>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var loaded = await DocumentLoader.LoadFileAsync(path, "environment definition", [Kind], cancellationToken);
        if (loaded.Value is not { Body: var body })
        {
            return new LoadResult<EnvironmentDefinition>(null, loaded.Errors);
        }

        // The schema guarantees these exist and have these shapes.
        return new LoadResult<EnvironmentDefinition>(
            new EnvironmentDefinition(
                (string)body["name"]!, (string)body["region"]!, (string)body["type"]!, body["offers"]!.ToObject<List<string>>()!,
                body["approval"] is { } approval ? ApprovalPolicy.FromJson(approval) : null),
            []);
    }
}
