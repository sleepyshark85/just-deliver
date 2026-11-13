using jd.resolver.catalog;
using Newtonsoft.Json.Linq;

namespace jd.resolver;

/// <summary>
/// Fills concept records from a document's roles (docs/architecture/documents.md section 3), located by <see cref="SchemaIndex.RolesOf"/>: each
/// value with where it was read. No kind or field is spelled here. A role's value belongs to the nearest object that holds one: the record's own
/// object, else the closest object enclosing it, so a value an owner holds counts for each of its parts. An absent field binds its schema's
/// <c>default</c>. Only schema-valid documents are bound.
/// </summary>
internal sealed class Binder(SchemaIndex index)
{
    /// <summary>The part owner <paramref name="document"/> is, named <paramref name="name"/>; its kind must hold the <see cref="Roles.Owner"/> role.</summary>
    public PartOwner Owner(Document document, string name)
    {
        var roles = index.RolesOf(document.Kind);
        if (!roles.Any(field => field.Role == Roles.Owner))
        {
            throw new InvalidOperationException($"Kind '{document.Kind}' binds no {Roles.Owner}; its documents own no parts.");
        }

        var values = new Values(document.Body, roles);
        var parts = roles.Where(field => field.Role is Roles.Part or Roles.RuntimePart)
            .SelectMany(field => document.Body.SelectTokens(field.Path).Select(part => Part(values, field, part)));
        return new PartOwner(document.Source, name, Strings(values.Nearest(Roles.Contract, document.Body)), [.. parts]);
    }

    private static Part Part(Values values, RoleField field, JToken part)
    {
        var exports = values.Nearest(Roles.PartExports, part) as JObject;
        return new Part
        {
            Location = part.Path,
            Scope = field.Scope ?? Text(values.Nearest(Roles.PartScope, part)),
            Keys = [.. field.Selects.Select(select => Key(values.Body, select, part))],
            Reads = Strings(values.Nearest(Roles.PartReads, part)),
            Nodes = Entries(values.Nearest(Roles.PartNodes, part)).ToDictionary(node => node.Name, node => Node(values, node.Value)),
            Exports = Entries(exports).ToDictionary(export => export.Name, export => Text(export.Value)),
            ExportsLocation = exports?.Path ?? part.Path,
        };
    }

    // The entry of the select map that encloses the part; the map is named after the field that holds it.
    private static SelectKey Key(JObject body, SelectField select, JToken part)
    {
        var maps = body.SelectTokens(select.Path).ToHashSet();
        var map = part.Ancestors().OfType<JObject>().First(maps.Contains);
        var entry = part.AncestorsAndSelf().OfType<JProperty>().First(property => property.Parent == map);
        return new SelectKey(select.Role, entry.Name, select.Fallback, map.Parent is JProperty field ? field.Name : map.Path, map.Path);
    }

    private static Node Node(Values values, JToken node)
    {
        var config = values.Nearest(Roles.NodeConfig, node) as JObject;
        return new Node(
            Text(values.Nearest(Roles.NodeTemplate, node)),
            Enum.Parse<NodeKind>(Text(values.Nearest(Roles.NodeKind, node)), ignoreCase: true),
            Entries(config).ToDictionary(entry => entry.Name, entry => entry.Value),
            node.Path,
            config?.Path ?? node.Path);
    }

    private static IEnumerable<JProperty> Entries(JToken? map) => map is JObject entries ? entries.Properties() : [];

    private static string Text(JToken? token) => (string?)token ?? string.Empty;

    private static List<string> Strings(JToken? token) => token is JArray list ? [.. list.Select(Text)] : [];

    // A document's role values: every token at a role's paths, and the role's default.
    private sealed class Values(JObject body, IReadOnlyList<RoleField> roles)
    {
        public JObject Body => body;

        // The role's value held by the object at, or by the closest object enclosing it; else the role's default, null when it has none.
        public JToken? Nearest(string role, JToken at)
        {
            var fields = roles.Where(field => field.Role == role).ToList();
            var held = fields.SelectMany(field => body.SelectTokens(field.Path)).ToList();
            return at.AncestorsAndSelf().Select(holder => held.FirstOrDefault(value => value.Parent?.Parent == holder)).FirstOrDefault(value => value is not null)
                ?? fields.Select(field => field.Default).FirstOrDefault(value => value is not null);
        }
    }
}
