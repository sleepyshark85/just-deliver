namespace jd.resolver.release;

/// <summary>One workload going into a release set.</summary>
/// <param name="File">Names the definition in its errors: the name its reader gave when creating, the release set when loading.</param>
/// <param name="Location">Prefix for locations inside the definition: empty for a definition its reader read, where the set embeds it in a release set.</param>
/// <param name="Sha256">Lowercase hex SHA-256 over the exact bytes of the definition.</param>
/// <param name="Definition">The definition text as decoded from those bytes (a byte order mark is kept, so it re-encodes to them).</param>
/// <param name="Entry">The manifest's or set's entry for the workload: its dependencies and where the entry's fields are held.</param>
internal sealed record WorkloadSource(string File, string Location, string Sha256, string Definition, ReleaseEntry Entry);
