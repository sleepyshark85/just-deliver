using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace jd.resolver.expressions;

// The built-in functions. Their behaviour is a format contract, documented in docs/architecture/resolver.md.
public static partial class BuiltIns
{
    internal const string NameFunction = "name";
    internal const string GuidFunction = "guid";

    /// <summary>Namespace of every <c>guid()</c> result. Changing it changes every generated id.</summary>
    public static readonly Guid GuidNamespace = new("8d6c1f0e-5b3a-4c7e-9a21-7e4f0b2d6c35");

    private const int HashLength = 6;

    private static readonly ConcurrentDictionary<string, Regex> AllowedClasses = new();

    [GeneratedRegex(@"\{(workload|id|env|hash)\}")]
    private static partial Regex Placeholder();

    /// <summary>Applies the naming rule for <paramref name="kind"/>; null (after calling <paramref name="fail"/>) when it cannot.</summary>
    internal static string? Name(ExpressionContext context, string kind, Action<string> fail)
    {
        if (!context.Naming.TryGetValue(kind, out var rule))
        {
            fail($"no naming rule for kind '{kind}'; known kinds: {string.Join(", ", context.Naming.Keys.Order())}.");
            return null;
        }

        // The catalog loader has checked that the class is a valid regex that allows the hash digits.
        var allowed = AllowedClasses.GetOrAdd(rule.Allowed, pattern => new Regex(pattern));

        var values = new Dictionary<string, string>
        {
            ["workload"] = context.WorkloadName,
            ["id"] = context.CurrentId,
            ["env"] = context.Environment.Name,
            ["hash"] = Hash(context, kind),
        };
        var name = Placeholder().Replace(rule.Pattern, m => values[m.Groups[1].Value]).ToLowerInvariant();
        name = string.Concat(name.Where(c => allowed.IsMatch(c.ToString())));
        if (name.Length > rule.MaxLength)
        {
            // Keep the readable prefix; the hash keeps truncated names unique.
            name = (name[..Math.Max(rule.MaxLength - HashLength, 0)] + values["hash"])[..rule.MaxLength];
        }

        return name;
    }

    internal static string Guid(IEnumerable<string> args) => Uuid5(GuidNamespace, string.Join('|', args)).ToString();

    private static string Hash(ExpressionContext context, string kind) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{context.WorkloadName}|{context.Environment.Name}|{context.CurrentId}|{kind}")))[..HashLength];

    // RFC 4122 section 4.3: SHA-1 over namespace (network byte order) + name; set version 5 and the RFC variant.
    public static Guid Uuid5(Guid ns, string name)
    {
        var nsBytes = new byte[16];
        ns.TryWriteBytes(nsBytes, bigEndian: true, out _);
        var bytes = SHA1.HashData([.. nsBytes, .. Encoding.UTF8.GetBytes(name)])[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
