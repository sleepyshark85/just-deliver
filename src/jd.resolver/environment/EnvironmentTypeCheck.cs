using jd.resolver.catalog;

namespace jd.resolver.environment;

/// <summary>
/// The catalog's rules for an environment's type (docs/architecture/components.md section 2): the type is declared, and a gated type's
/// environment carries the approval block (security boundary 13). The block is what gates, not the type. The type is another document set's
/// name, so this stays code (docs/architecture/documents.md section 2, concept 11); the locations and the block's field come from the binding.
/// </summary>
public static class EnvironmentTypeCheck
{
    /// <summary>Every way <paramref name="environment"/> breaks the catalog's rules, located in <paramref name="file"/>; empty when it keeps them.</summary>
    public static IReadOnlyList<LoadError> Check(Catalog catalog, EnvironmentDescriptor environment, string file)
    {
        var type = environment.Type;
        if (!catalog.EnvironmentTypes.TryGetValue(type, out var declared))
        {
            var known = string.Join(", ", catalog.EnvironmentTypes.Keys.Order(StringComparer.Ordinal));
            return [new LoadError(file, environment.Location(Roles.EnvironmentType), $"'{type}' is not an environment type of the catalog (declared: {known}).")];
        }

        // Absent, the block is located by the field that would hold it, which is also how the message names it.
        var block = environment.Location(Roles.EnvironmentApproval);
        return declared.Approval && environment.Approval is null
            ? [new LoadError(file, block, $"environment type '{type}' requires an '{block}' block; an environment of this type cannot be ungated.")]
            : [];
    }
}
