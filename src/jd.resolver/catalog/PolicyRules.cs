using jd.resolver.expansion;
using jd.resolver.expressions;

namespace jd.resolver.catalog;

/// <summary>
/// What a policy's shape must be (docs/architecture/documents.md section 2, concept 10), over the policies bound: it applies to node scope or
/// workload scope (resolver.md, Policy scope), and no path it sets or defaults is under another of its own. Code, since JSON Schema cannot give
/// these messages (documents.md section 3); the field names and locations come from the binding.
/// </summary>
internal static class PolicyRules
{
    // A workload-scope policy applies to no requirement's node, so the only criteria it can match are the environment's.
    private static readonly string[] WorkloadCriteria = [Roles.EnvironmentType];

    // Every path of a workload-scope policy is a field of the runtime node, so it starts with that node's name.
    private static readonly string RuntimePrefix = ExpressionEvaluator.RuntimeNode + ConfigLeaves.PathSeparator;

    /// <summary>Every policy of <paramref name="policies"/> that fits neither scope, then every path that overlaps another in the same policy.</summary>
    public static IReadOnlyList<LoadError> Check(IReadOnlyList<Policy> policies) => [.. policies.SelectMany(ScopeErrors), .. policies.SelectMany(OverlapErrors)];

    // A policy that fits neither scope would silently do nothing.
    private static IEnumerable<LoadError> ScopeErrors(Policy policy)
    {
        var scope = policy.Scope;
        var workloadScope = $"{scope.Name}: {Policy.WorkloadScope}";
        LoadError Error(string location, string message) => new(policy.Source, location, message);

        if (scope.Value is { } named && named != Policy.WorkloadScope)
        {
            yield return Error(scope.Location, $"'{named}' is not a policy scope; the only '{scope.Name}' a policy can match is '{Policy.WorkloadScope}'.");
        }
        else if (scope.Value is null)
        {
            if (policy.Runtime is { Value: not null } runtime)
            {
                yield return Error(runtime.Location, $"'{runtime.Name}' needs '{workloadScope}'; node-scope policies match {Listed([.. policy.Criteria.Select(criterion => criterion.Name)])}.");
            }

            if (policy.Require is { Value: not null } require)
            {
                yield return Error(require.Location, $"a node-scope policy cannot require a component; use '{workloadScope}' to make every workload require it.");
            }
        }
        else
        {
            var nodeCriteria = policy.Criteria.Where(criterion => criterion.Value is not null && !WorkloadCriteria.Contains(criterion.Role))
                .Select(criterion => criterion.Name).Order(StringComparer.Ordinal).ToList();
            if (nodeCriteria.Count > 0)
            {
                yield return Error(policy.CriteriaLocation, $"a '{workloadScope}' policy cannot also match {string.Join(", ", nodeCriteria)}.");
            }

            foreach (var entry in Fields(policy).SelectMany(field => field.Entries).Where(entry => !entry.Path.StartsWith(RuntimePrefix, StringComparison.Ordinal)))
            {
                yield return Error(entry.Location, $"a '{workloadScope}' policy can only set or default fields under '{RuntimePrefix}', not '{entry.Path}'.");
            }
        }
    }

    // Inside one policy, a path and one of its sub-paths would make the result depend on application order.
    private static IEnumerable<LoadError> OverlapErrors(Policy policy) =>
        from field in Fields(policy)
        from outer in field.Entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)
        from inner in field.Entries.Select(entry => entry.Path).Order(StringComparer.Ordinal).Where(path => path.StartsWith(outer.Path + ConfigLeaves.PathSeparator, StringComparison.Ordinal))
        select new LoadError(policy.Source, outer.Location, $"overlaps '{inner}' in the same policy; give one of them.");

    // The paths the policy sets, then those it defaults.
    private static PolicyEntries[] Fields(Policy policy) => [policy.Set, policy.Default];

    // Names as a sentence lists them: "a", "a and b", "a, b and c".
    private static string Listed(IReadOnlyList<string> names) => names.Count < 2 ? string.Join(string.Empty, names) : $"{string.Join(", ", names.SkipLast(1))} and {names[^1]}";
}
