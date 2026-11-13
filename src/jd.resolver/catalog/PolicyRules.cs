using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>
/// What a policy's shape must be (docs/architecture/documents.md section 2, concept 10), over the policies read: it applies to node scope or
/// workload scope (resolver.md, Policy scope), and no path it sets or defaults is under another of its own. Code until S24d binds the policy roles.
/// </summary>
internal static class PolicyRules
{
    /// <summary>Every policy of <paramref name="policies"/> that fits neither scope, then every path that overlaps another in the same policy.</summary>
    public static IReadOnlyList<LoadError> Check(IReadOnlyList<Policy> policies) => [.. policies.SelectMany(ScopeErrors), .. policies.SelectMany(OverlapErrors)];

    // A policy that fits neither scope would silently do nothing.
    private static IEnumerable<LoadError> ScopeErrors(Policy policy)
    {
        var criteria = policy.Match.Criteria;
        var kind = criteria.GetValueOrDefault(MatchCriteria.KindKey);
        LoadError Error(string location, string message) => new(policy.Source, location, message);

        if (kind is not null && kind != MatchCriteria.RuntimeKind)
        {
            yield return Error("match.kind", $"'{kind}' is not a policy scope; the only 'kind' a policy can match is '{MatchCriteria.RuntimeKind}'.");
        }
        else if (kind is null)
        {
            if (criteria.ContainsKey(MatchCriteria.RuntimeKey))
            {
                yield return Error("match.runtime", $"'runtime' needs 'kind: {MatchCriteria.RuntimeKind}'; node-scope policies match type, class, environmentType and template.");
            }

            if (policy.Require is not null)
            {
                yield return Error("require", $"a node-scope policy cannot require a component; use 'kind: {MatchCriteria.RuntimeKind}' to make every workload require it.");
            }
        }
        else
        {
            var nodeCriteria = criteria.Keys.Where(k => k is MatchCriteria.TypeKey or MatchCriteria.ClassKey or MatchCriteria.TemplateKey).Order().ToList();
            if (nodeCriteria.Count > 0)
            {
                yield return Error("match", $"a 'kind: {MatchCriteria.RuntimeKind}' policy cannot also match {string.Join(", ", nodeCriteria)}.");
            }

            foreach (var (field, path) in from field in Fields(policy)
                                          from path in field.Entries.Keys.Where(key => !key.StartsWith(MatchCriteria.RuntimeKey + ".", StringComparison.Ordinal))
                                          select (field.Name, path))
            {
                yield return Error($"{field}.{path}", $"a 'kind: {MatchCriteria.RuntimeKind}' policy can only set or default fields under '{MatchCriteria.RuntimeKey}.', not '{path}'.");
            }
        }
    }

    // Inside one policy, a path and one of its sub-paths would make the result depend on application order.
    private static IEnumerable<LoadError> OverlapErrors(Policy policy) =>
        from field in Fields(policy)
        from outer in field.Entries.Keys.Order(StringComparer.Ordinal)
        from inner in field.Entries.Keys.Order(StringComparer.Ordinal).Where(key => key.StartsWith(outer + ".", StringComparison.Ordinal))
        select new LoadError(policy.Source, $"{field.Name}.{outer}", $"overlaps '{inner}' in the same policy; give one of them.");

    // The paths the policy sets, then those it defaults, each under the name of the field that holds them.
    private static (string Name, IReadOnlyDictionary<string, JToken> Entries)[] Fields(Policy policy) => [("set", policy.Set), ("default", policy.Default)];
}
