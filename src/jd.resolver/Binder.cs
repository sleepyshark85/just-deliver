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
    public PartOwner ReadOwner(Document document, string name)
    {
        var roles = index.RolesOf(document.Kind);
        if (!roles.Any(field => field.Role == Roles.Owner))
        {
            throw new InvalidOperationException($"Kind '{document.Kind}' binds no {Roles.Owner}; its documents own no parts.");
        }

        var values = new Values(document.Body, roles);
        var parts = roles.Where(field => field.Role is Roles.Part or Roles.RuntimePart)
            .SelectMany(field => document.Body.SelectTokens(field.Path).Select(part => ReadPart(values, field, part)));
        return new PartOwner(document.Source, name, Strings(values.Nearest(Roles.Contract, document.Body)), [.. parts]);
    }

    /// <summary>The catalog version <paramref name="document"/> holds (<see cref="Roles.CatalogVersion"/>); empty when it holds none.</summary>
    public string ReadVersion(Document document) => Text(ValuesOf(document).Nearest(Roles.CatalogVersion, document.Body));

    /// <summary>
    /// The environment types <paramref name="document"/> declares, by name: each object that holds an <see cref="Roles.EnvironmentTypeLayer"/>,
    /// named by the key it is held under, gated when it holds a true <see cref="Roles.EnvironmentTypeGated"/>.
    /// </summary>
    public IReadOnlyDictionary<string, EnvironmentType> ReadEnvironmentTypes(Document document)
    {
        var values = ValuesOf(document);
        return values.Every(Roles.EnvironmentTypeLayer).Select(layer => (Layer: layer, Type: Holder(layer))).ToDictionary(
            declared => KeyOf(declared.Type),
            declared => new EnvironmentType((bool?)values.Nearest(Roles.EnvironmentTypeGated, declared.Type) ?? false, Text(declared.Layer)));
    }

    /// <summary>The naming rules <paramref name="document"/> holds (<see cref="Roles.NamingRules"/>), by resource kind.</summary>
    public IReadOnlyDictionary<string, NamingRule> ReadNamingRules(Document document)
    {
        var values = ValuesOf(document);
        return Entries(values.Nearest(Roles.NamingRules, document.Body)).ToDictionary(rule => rule.Name, rule => ReadNamingRule(values, rule.Value));
    }

    /// <summary>The role definitions <paramref name="document"/> holds (<see cref="Roles.RoleDefinitions"/>), by role name.</summary>
    public IReadOnlyDictionary<string, string> ReadRoleDefinitions(Document document) =>
        Entries(ValuesOf(document).Nearest(Roles.RoleDefinitions, document.Body)).ToDictionary(role => role.Name, role => Text(role.Value));

    /// <summary>Each <see cref="Roles.GrantTargetInput"/> <paramref name="document"/> holds, by the grant template it is held under.</summary>
    public IReadOnlyDictionary<string, string> ReadGrantTargets(Document document) =>
        ValuesOf(document).Every(Roles.GrantTargetInput).ToDictionary(KeyOf, Text);

    private Values ValuesOf(Document document) => new(document.Body, index.RolesOf(document.Kind));

    private static Part ReadPart(Values values, RoleField field, JToken part)
    {
        var exports = values.Nearest(Roles.PartExports, part) as JObject;
        var reads = values.Nearest(Roles.PartReads, part);
        return new Part
        {
            Location = part.Path,
            Scope = field.Scope ?? Text(values.Nearest(Roles.PartScope, part)),
            Keys = [.. field.Selects.Select(select => ReadKey(values.Body, select, part))],
            Reads = Strings(reads),
            ReadsLocation = reads?.Path ?? part.Path,
            Nodes = Entries(values.Nearest(Roles.PartNodes, part)).ToDictionary(node => node.Name, node => ReadNode(values, node.Value)),
            Exports = Entries(exports).ToDictionary(export => export.Name, export => Text(export.Value)),
            ExportsLocation = exports?.Path ?? part.Path,
        };
    }

    // The entry of the select map that encloses the part; the map is named after the field that holds it.
    // Safe: a select annotation sits on a property's schema, so the map is that property's value and its parent is the property.
    private static SelectKey ReadKey(JObject body, SelectField select, JToken part)
    {
        var maps = body.SelectTokens(select.Path).ToHashSet();
        var map = part.Ancestors().OfType<JObject>().First(maps.Contains);
        var entry = part.AncestorsAndSelf().OfType<JProperty>().First(property => property.Parent == map);
        return new SelectKey(select.Role, entry.Name, select.Fallback, ((JProperty)map.Parent!).Name, map.Path);
    }

    private static Node ReadNode(Values values, JToken node)
    {
        var config = values.Nearest(Roles.NodeConfig, node) as JObject;
        var template = values.Nearest(Roles.NodeTemplate, node);
        return new Node(
            Text(template),
            Enum.Parse<NodeKind>(Text(values.Nearest(Roles.NodeKind, node)), ignoreCase: true),
            Entries(config).ToDictionary(entry => entry.Name, entry => entry.Value),
            node.Path,
            config?.Path ?? node.Path,
            template?.Path ?? node.Path);
    }

    // A rule without allowed characters is located where the rule is, as a node without config is.
    private static NamingRule ReadNamingRule(Values values, JToken rule)
    {
        var allowed = values.Nearest(Roles.NamingAllowed, rule);
        return new NamingRule(Text(values.Nearest(Roles.NamingPattern, rule)), (int?)values.Nearest(Roles.NamingMaxLength, rule) ?? 0, Text(allowed))
        {
            AllowedLocation = allowed?.Path ?? rule.Path,
        };
    }

    // The object holding a value, and the key a map holds a value under. Safe: the roles read through them are on an object's fields or a map's
    // values (schemas/catalog), never on a list's items, so a value's parent is a property and its grandparent an object.
    private static JObject Holder(JToken value) => (JObject)value.Parent!.Parent!;

    private static string KeyOf(JToken value) => ((JProperty)value.Parent!).Name;

    private static IEnumerable<JProperty> Entries(JToken? map) => map is JObject entries ? entries.Properties() : [];

    private static string Text(JToken? token) => (string?)token ?? string.Empty;

    private static List<string> Strings(JToken? token) => token is JArray list ? [.. list.Select(Text)] : [];

    // A document's role values: every token at a role's paths, and the role's default.
    private sealed class Values(JObject body, IReadOnlyList<RoleField> roles)
    {
        public JObject Body => body;

        // Every value of the role in the document, in document order per field.
        public List<JToken> Every(string role) => [.. roles.Where(field => field.Role == role).SelectMany(field => body.SelectTokens(field.Path))];

        // The role's value held by the object at, or by the closest object enclosing it; else the role's default, null when it has none.
        public JToken? Nearest(string role, JToken at)
        {
            var held = Every(role);
            return at.AncestorsAndSelf().Select(holder => held.FirstOrDefault(value => value.Parent?.Parent == holder)).FirstOrDefault(value => value is not null)
                ?? roles.Where(field => field.Role == role).Select(field => field.Default).FirstOrDefault(value => value is not null);
        }
    }
}
