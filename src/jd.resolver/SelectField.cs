namespace jd.resolver;

/// <summary>A map whose entry is chosen by a role's value (<c>select</c>; docs/architecture/documents.md section 2, concept 6).</summary>
/// <param name="Role">The role whose value keys the entry.</param>
/// <param name="Path">Where the map is in a document of the kind, as a JSONPath from its root.</param>
/// <param name="Fallback">The key of the entry used when no key equals the value (<c>fallback</c>); null when the map has none.</param>
internal sealed record SelectField(string Role, string Path, string? Fallback);
