using jd.resolver.expressions;

namespace jd.resolver.catalog;

/// <summary>
/// What a component's class parts promise (docs/architecture/documents.md section 2, concepts 4, 6 and 8), over the bound components. A
/// workload's class must be valid in every environment, so every implementation declares the same classes; every class part exports exactly
/// the component's contract, so <c>${resource.&lt;id&gt;.&lt;export&gt;}</c> resolves in every environment; and no export reads another
/// requirement's. Each error is located where the binder read the class map or the exports.
/// </summary>
internal static class ContractRules
{
    /// <summary>Every implementation of <paramref name="components"/> whose classes differ from the first implementation's.</summary>
    public static IReadOnlyList<LoadError> CheckSameClasses(IEnumerable<PartOwner> components) =>
        [.. components.SelectMany(component =>
        {
            // The class map's keys are the inner select keys of the class parts, grouped by the implementation they are in.
            var implementations = component.Parts.Where(part => part.Keys.Count > 1).GroupBy(part => part.Keys[0].Key)
                .Select(implementation => (implementation.Key, Location: implementation.First().Keys[1].MapLocation,
                    Classes: implementation.Select(part => part.Keys[1].Key).ToList())).ToList();
            var first = implementations[0];
            return implementations.Where(implementation => !implementation.Classes.ToHashSet().SetEquals(first.Classes)).Select(implementation => new LoadError(
                component.Source, implementation.Location,
                $"declares {string.Join(", ", implementation.Classes)}; every implementation must declare the classes of '{first.Key}' ({string.Join(", ", first.Classes)})."));
        })];

    /// <summary>
    /// Every export of a class part of <paramref name="components"/> (a part of none of <paramref name="layers"/>) that reads a requirement's
    /// export, then every class part whose exports differ from its component's contract.
    /// </summary>
    public static IReadOnlyList<LoadError> CheckExports(IEnumerable<PartOwner> components, IReadOnlyList<string> layers) =>
        [.. from component in components
            from part in component.Parts.Where(part => !layers.Contains(part.Scope))
            from error in ExportErrors(component, part)
            select error];

    private static IEnumerable<LoadError> ExportErrors(PartOwner component, Part part)
    {
        // An export is evaluated in its own requirement's scope, where another requirement's exports are out of reach.
        foreach (var export in part.Exports.Where(export => ExpressionEvaluator.ReadsResource(export.Value)).Select(export => export.Key))
        {
            yield return new LoadError(component.Source, part.ExportLocations[export],
                "an export cannot reference ${resource.<id>.<export>}; it may read its own nodes' outputs, what its component uses, env.* and runtime.*.");
        }

        var missing = component.Contract.Where(export => !part.Exports.ContainsKey(export)).ToList();
        var extra = part.Exports.Keys.Where(export => !component.Contract.Contains(export)).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            var problems = new[] { missing.Count > 0 ? $"missing {string.Join(", ", missing)}" : null, extra.Count > 0 ? $"not in the contract: {string.Join(", ", extra)}" : null };
            yield return new LoadError(component.Source, part.ExportsLocation,
                $"must export exactly the contract of component '{component.Name}' ({string.Join(", ", component.Contract)}); {string.Join("; ", problems.OfType<string>())}.");
        }
    }
}
