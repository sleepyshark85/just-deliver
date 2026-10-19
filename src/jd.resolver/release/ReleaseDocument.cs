using System.Security.Cryptography;

namespace jd.resolver.release;

/// <summary>What the release files share: the hash that pins a definition or a set.</summary>
internal static class ReleaseDocument
{
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
