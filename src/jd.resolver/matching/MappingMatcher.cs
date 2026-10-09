using jd.resolver.catalog;

namespace jd.resolver.matching;

/// <summary>A workload requirement with the format defaults applied: <c>id</c> falls back to the type, <c>class</c> to <see cref="DefaultClass"/>.</summary>
public sealed record Requirement(string Id, string Type, string Class)
{
    public const string DefaultClass = "standard";
}

/// <summary>
/// Selects the catalog mapping that applies to a requirement, or to a workload's runtime. Pure. Rules: docs/architecture/resolver.md (Engine rules, Matching).
/// </summary>
public static class MappingMatcher
{
    /// <summary>
    /// Returns the most specific mapping whose every <c>match</c> criterion equals the requirement's value. A tie at the top
    /// or no match is added to <paramref name="errors"/> at <paramref name="file"/>/<paramref name="location"/>, and null returned.
    /// </summary>
    public static Mapping? Select(IEnumerable<Mapping> mappings, Requirement requirement, string tier, string file, string location, ICollection<LoadError> errors)
    {
        // Any other key (kind, runtime) belongs to runtime mappings and never matches a requirement.
        var provided = new Dictionary<string, string>
        {
            [MatchCriteria.TypeKey] = requirement.Type,
            [MatchCriteria.ClassKey] = requirement.Class,
            [MatchCriteria.TierKey] = tier,
        };
        var described = $"requirement '{requirement.Id}' (type '{requirement.Type}', class '{requirement.Class}', tier '{tier}')";
        return Select(mappings, provided, described, file, location, errors);
    }

    /// <summary>
    /// Returns the most specific runtime mapping (<c>kind: runtime</c>, optionally <c>tier</c>) of a workload, with the errors of
    /// <see cref="Select(IEnumerable{Mapping}, Requirement, string, string, string, ICollection{LoadError})"/>. The <c>runtime</c> key
    /// is reserved until a workload can name a runtime, so a mapping using it is never selected.
    /// </summary>
    public static Mapping? SelectRuntime(IEnumerable<Mapping> mappings, string tier, string file, ICollection<LoadError> errors)
    {
        var provided = new Dictionary<string, string>
        {
            [MatchCriteria.KindKey] = MatchCriteria.RuntimeKind,
            [MatchCriteria.TierKey] = tier,
        };
        return Select(mappings, provided, $"the runtime of the workload (tier '{tier}')", file, MatchCriteria.RuntimeKind, errors);
    }

    private static Mapping? Select(IEnumerable<Mapping> mappings, Dictionary<string, string> provided, string described, string file, string location, ICollection<LoadError> errors)
    {
        var matching = mappings
            .Where(m => m.Match.Criteria.All(c => provided.TryGetValue(c.Key, out var value) && value == c.Value))
            .ToList();
        if (matching.Count == 0)
        {
            errors.Add(new LoadError(file, location, $"no mapping matches {described}."));
            return null;
        }

        var specificity = matching.Max(m => m.Match.Criteria.Count);
        var best = matching.Where(m => m.Match.Criteria.Count == specificity).ToList();
        if (best.Count > 1)
        {
            var files = string.Join(", ", best.Select(m => m.Source).Order(StringComparer.Ordinal));
            errors.Add(new LoadError(file, location, $"{described} matches {best.Count} mappings equally well: {files}."));
            return null;
        }

        return best[0];
    }
}
