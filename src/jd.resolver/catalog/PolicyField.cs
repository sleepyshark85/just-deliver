namespace jd.resolver.catalog;

/// <summary>A field of a policy that holds a role, as the binder read it.</summary>
/// <param name="Name">The field's name, as messages name it.</param>
/// <param name="Location">Where the field was read; its name when the policy leaves it out.</param>
/// <param name="Value">The field's value; null when the policy leaves it out.</param>
public sealed record PolicyField(string Name, string Location, string? Value);
