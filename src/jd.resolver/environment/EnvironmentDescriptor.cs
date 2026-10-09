namespace jd.resolver.environment;

/// <summary>
/// What an environment publishes for <c>${env.…}</c> resolution. Format: schemas/environment.schema.json.
/// <see cref="Values"/> is keyed by the flattened dot path of each scalar leaf.
/// </summary>
public sealed record EnvironmentDescriptor(
    string Name,
    string Region,
    string Tier,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> Grantable)
{
    // Top-level fields addressed as env.<key>; values may not shadow them.
    public const string NameKey = "name";
    public const string RegionKey = "region";
    public const string TierKey = "tier";

    public static readonly IReadOnlyList<string> ReservedKeys = [NameKey, RegionKey, TierKey];

    /// <summary>
    /// Resolves the part of an <c>env.&lt;path&gt;</c> reference after <c>env.</c>: the reserved keys come from the
    /// top-level fields, any other dot path from <see cref="Values"/>. Only scalar leaves resolve.
    /// </summary>
    public bool TryGet(string path, out string value)
    {
        var found = path switch
        {
            NameKey => Name,
            RegionKey => Region,
            TierKey => Tier,
            _ => Values.GetValueOrDefault(path),
        };
        value = found ?? string.Empty;
        return found is not null;
    }
}
