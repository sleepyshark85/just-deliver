namespace jd.resolver.catalog;

/// <summary>A select map with no entry for a value and none for its fallback.</summary>
/// <param name="Map">The map's name in its file.</param>
/// <param name="Value">The value no key equals.</param>
/// <param name="Fallback">The map's fallback key; null when it has none.</param>
/// <param name="Keys">The keys the map has.</param>
/// <param name="Within">The keys of the maps above it that were selected, outermost first: the platform for a types map.</param>
internal sealed record SelectMiss(string Map, string Value, string? Fallback, IReadOnlyList<string> Keys, IReadOnlyList<string> Within)
{
    /// <summary>The miss as a message about <paramref name="owner"/> (documents.md section 4).</summary>
    public string Message(string owner) =>
        $"{owner}{(Within.Count == 0 ? string.Empty : $" on {string.Join("/", Within.Select(key => $"'{key}'"))}")} has no {Map} entry for '{Value}'{(Fallback is null ? string.Empty : $" and no '{Fallback}' entry")} (keys: {string.Join(", ", Keys)}).";
}
