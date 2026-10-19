using System.Security.Cryptography;

namespace jd.resolver.release;

/// <summary>The hash the release documents pin content by: a set pins each workload definition, a record its set and its graphs.</summary>
public static class ReleaseHash
{
    /// <summary>The lowercase hex SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
