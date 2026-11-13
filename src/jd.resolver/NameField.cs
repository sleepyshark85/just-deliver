namespace jd.resolver;

/// <summary>A field that defines or refers to names of one namespace: a string, a list of strings, or a map's keys.</summary>
/// <param name="Namespace">The namespace, as the schema spells it.</param>
/// <param name="Path">Where the field's values are in a document of the kind, as a JSONPath from its root.</param>
/// <param name="Fallback">For a reference, the map key the schema names its map's fallback: a key, not a name, so not looked up.</param>
internal sealed record NameField(string Namespace, string Path, string? Fallback);
