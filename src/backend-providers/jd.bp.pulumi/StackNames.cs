using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace jd.bp.pulumi;

/// <summary>
/// Maps the graph's stack name to the name used in Pulumi, which limits stack names to 100 characters
/// of <c>[A-Za-z0-9_.-]</c>. Names that fit pass through unchanged. Longer names become the first
/// <see cref="PrefixLength"/> characters, a dash and the first <see cref="HashLength"/> hex characters of
/// the SHA-256 of the full name: deterministic, and different inputs collide only by hash collision.
/// </summary>
internal static partial class StackNames
{
    internal const int MaxLength = 100;
    private const int HashLength = 16;
    private const int PrefixLength = MaxLength - HashLength - 1;

    public static string ToPulumi(string graphStack)
    {
        // The name is also a directory name, so it must not start with '.' or contain separators.
        if (!Valid().IsMatch(graphStack))
        {
            throw new ArgumentException($"Stack name '{graphStack}' must consist of letters, digits, '_', '.' and '-' and not start with '.' or '-'.", nameof(graphStack));
        }

        if (graphStack.Length <= MaxLength)
        {
            return graphStack;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(graphStack)))[..HashLength];
        return $"{graphStack[..PrefixLength]}-{hash}";
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]*$")]
    private static partial Regex Valid();
}
