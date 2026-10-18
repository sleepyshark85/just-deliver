using jd.resolver.catalog;

namespace jd.resolver.environment;

/// <summary>
/// The catalog's rules for an environment definition's or descriptor's <c>type</c> (docs/architecture/components.md section 2): the type is
/// declared, and a type with <c>approval: true</c> carries the approval block (security boundary 13). The block is what gates, not the type.
/// </summary>
public static class EnvironmentTypeCheck
{
    public static IReadOnlyList<LoadError> Check(Catalog catalog, string type, ApprovalPolicy? approval, string file)
    {
        if (!catalog.EnvironmentTypes.TryGetValue(type, out var declared))
        {
            var known = string.Join(", ", catalog.EnvironmentTypes.Keys.Order(StringComparer.Ordinal));
            return [new LoadError(file, "type", $"'{type}' is not an environment type of the catalog (declared: {known}).")];
        }

        return declared.Approval && approval is null
            ? [new LoadError(file, "approval", $"environment type '{type}' requires an 'approval' block; an environment of this type cannot be ungated.")]
            : [];
    }
}
