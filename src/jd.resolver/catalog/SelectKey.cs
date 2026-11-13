namespace jd.resolver.catalog;

/// <summary>The entry of a select map a part is in (docs/architecture/documents.md section 2, concept 6).</summary>
/// <param name="Role">The role whose value chooses the map's entry.</param>
/// <param name="Key">The entry's key.</param>
/// <param name="Fallback">The key of the entry used when no key equals the value; null when the map has none.</param>
/// <param name="Map">The map's name in its file, for messages.</param>
/// <param name="MapLocation">Where the map is in its file.</param>
public sealed record SelectKey(string Role, string Key, string? Fallback, string Map, string MapLocation);
