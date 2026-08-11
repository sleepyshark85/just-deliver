using System.Text.RegularExpressions;

namespace jd.resolver;

/// <summary>
/// Substitutes ${...} expressions inside parameter values. Interpolation is partial:
/// "rg-${workload.metadata.name}-${env.name}" contains two expressions inside one
/// literal, so this is string templating rather than a lookup.
/// </summary>
public static class ExpressionTemplate
{
    private static readonly Regex Expression = new(@"\$\{([a-zA-Z0-9_.\-]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// Replaces every expression whose prefix <paramref name="resolve"/> handles. Returning
    /// null from <paramref name="resolve"/> leaves the expression in place verbatim, which is
    /// how ${deployment.*} survives resolution to be filled in later by the executor.
    /// </summary>
    public static string Substitute(string value, Func<string, string?> resolve) =>
        Expression.Replace(value, match => resolve(match.Groups[1].Value) ?? match.Value);

    /// <summary>Every expression path present in the value.</summary>
    public static IEnumerable<string> Paths(string value) =>
        Expression.Matches(value).Select(m => m.Groups[1].Value);

    /// <summary>True when any expression remains unresolved.</summary>
    public static bool HasExpressions(string value) => Expression.IsMatch(value);

    /// <summary>
    /// Walks a dotted path into nested dictionaries. Used for ${workload.*}, where the
    /// path is against the deserialised document rather than a flat table.
    /// </summary>
    public static string? Lookup(IReadOnlyDictionary<string, object?> root, string path)
    {
        object? current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is IReadOnlyDictionary<string, object?> map && map.TryGetValue(segment, out var next))
            {
                current = next;
            }
            else
            {
                return null;
            }
        }

        return current as string;
    }
}
