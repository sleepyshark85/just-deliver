namespace jd.resolver.catalog;

/// <summary>
/// A catalog document that owns parts (docs/architecture/documents.md section 3): a component, a shared resource or a runtime, filled by the
/// binder. <see cref="Contract"/>: the exports every class part exports exactly (empty for an owner without one).
/// </summary>
public sealed record PartOwner(string Source, string Name, IReadOnlyList<string> Contract, IReadOnlyList<Part> Parts)
{
    /// <summary>The shared resources any of its parts reads.</summary>
    public IReadOnlyList<string> Reads => [.. Parts.SelectMany(part => part.Reads).Distinct()];

    /// <summary>
    /// The parts <paramref name="values"/> (role to value) select and <paramref name="inScope"/> keeps. Each select map, outermost first, keeps
    /// the entry keyed by its role's value, else its fallback entry; a map whose role has no value keeps every entry. Scopes are filtered
    /// after selecting, so a map keeps the entry the value names even when that entry has no part of the scope. The miss is set, and no part
    /// is, when a map has neither entry.
    /// </summary>
    public (IReadOnlyList<Part> Parts, SelectMiss? Miss) Select(IReadOnlyDictionary<string, string> values, Func<string, bool> inScope)
    {
        IReadOnlyList<Part> selected = Parts;
        for (var depth = 0; selected.Where(part => part.Keys.Count > depth).ToList() is [var first, ..] level; depth++)
        {
            var map = first.Keys[depth];
            if (!values.TryGetValue(map.Role, out var value))
            {
                continue;
            }

            var keys = level.Select(part => part.Keys[depth].Key).Distinct().ToList();
            var chosen = keys.Contains(value) ? value : map.Fallback;
            if (chosen is null || !keys.Contains(chosen))
            {
                return ([], new SelectMiss(map.Map, value, map.Fallback, keys));
            }

            selected = [.. selected.Where(part => part.Keys.Count <= depth || part.Keys[depth].Key == chosen)];
        }

        return ([.. selected.Where(part => inScope(part.Scope))], null);
    }
}
