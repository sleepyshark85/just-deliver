using Newtonsoft.Json.Linq;

namespace jd.resolver;

/// <summary>A field that holds a role (<c>is</c>; docs/architecture/documents.md section 3).</summary>
/// <param name="Role">The role, one of Appendix B.</param>
/// <param name="Path">Where the field's values are in a document of the kind, as a JSONPath from its root.</param>
/// <param name="Scope">The scope the schema states for a part at this path (<c>scope</c>); null when it states none.</param>
/// <param name="Default">The value an absent field binds (the schema's <c>default</c>); null when there is none.</param>
/// <param name="Selects">The select maps whose entries the path is in, outermost first.</param>
internal sealed record RoleField(string Role, string Path, string? Scope, JToken? Default, IReadOnlyList<SelectField> Selects);
