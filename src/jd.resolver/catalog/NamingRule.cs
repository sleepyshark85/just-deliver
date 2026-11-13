namespace jd.resolver.catalog;

/// <summary>How a resource kind's names are built: <see cref="Pattern"/>'s placeholders filled, characters outside the <see cref="Allowed"/> pattern dropped, and cut to <see cref="MaxLength"/> keeping a hash.</summary>
public sealed record NamingRule(string Pattern, int MaxLength, string Allowed)
{
    /// <summary>Where <see cref="Allowed"/> is in the rule's file, for the load check; empty for a rule no file holds.</summary>
    public string AllowedLocation { get; init; } = string.Empty;
}
