namespace jd.resolver.catalog;

/// <summary>
/// How the release flow names a revision: <see cref="Prefix"/>, then <see cref="ConfigDigits"/> hex digits of the config's hash and
/// <see cref="SaltDigits"/> of a salt. The runtime's data states it, so no runtime's naming rule is in code.
/// </summary>
public sealed record RevisionSuffix(string Prefix, int ConfigDigits, int SaltDigits)
{
    /// <summary>How many characters every suffix takes.</summary>
    public int Length => Prefix.Length + ConfigDigits + SaltDigits;
}
