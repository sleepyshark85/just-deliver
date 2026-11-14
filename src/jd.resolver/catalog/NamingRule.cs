using System.Text.RegularExpressions;

namespace jd.resolver.catalog;

/// <summary>How a resource kind's names are built: <see cref="Pattern"/>'s placeholders filled, characters outside the <see cref="Allowed"/> pattern dropped, and cut to <see cref="MaxLength"/> keeping a hash.</summary>
public sealed record NamingRule(string Pattern, int MaxLength, string Allowed)
{
    // A truncated name ends in a lowercase hex hash that is appended after filtering (concept 4, name()), so every rule must allow those digits.
    private const string HashDigits = "0123456789abcdef";

    /// <summary>Where <see cref="Allowed"/> is in the rule's file, for the load check; empty for a rule no file holds.</summary>
    public string AllowedLocation { get; init; } = string.Empty;

    /// <summary>The problem with <see cref="Allowed"/>, read from <paramref name="source"/>: not a regex, or one rejecting a hash digit; null when there is none.</summary>
    internal LoadError? CheckAllowed(string source)
    {
        try
        {
            var regex = new Regex(Allowed);
            var rejected = HashDigits.Where(digit => !regex.IsMatch(digit.ToString())).ToList();
            return rejected.Count == 0 ? null : new LoadError(source, AllowedLocation, $"'{Allowed}' must allow every hash digit 0-9a-f; it rejects {string.Concat(rejected)}.");
        }
        catch (ArgumentException e)
        {
            return new LoadError(source, AllowedLocation, $"'{Allowed}' is not a valid regex character class: {e.Message}");
        }
    }
}
