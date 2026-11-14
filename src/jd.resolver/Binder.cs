using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.workload;
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

    /// <summary>
    /// The rollout of each part of <paramref name="runtime"/>, which <paramref name="document"/> holds: the probe (<c>probe.*</c>) and the release
    /// block (<c>rollout.*</c>) inside the part, and where the load checks locate their errors.
    /// </summary>
    public IReadOnlyList<Rollout> ReadRollouts(Document document, PartOwner runtime)
    {
        var values = ValuesOf(document);
        return [.. runtime.Parts.Select(part => ReadRollout(values, runtime, part))];
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

    /// <summary>
    /// The environment <paramref name="document"/> is, a definition or a descriptor, by the <c>env.*</c> roles: its values as dot paths, its
    /// approval policy when it holds the block (the <c>approval.*</c> roles), and where each role of its kind was read (the roles of an absent block
    /// are located by their leaf names).
    /// </summary>
    public EnvironmentDescriptor ReadEnvironment(Document document)
    {
        var values = ValuesOf(document);
        JToken? Read(string role) => values.Nearest(role, document.Body);
        return new EnvironmentDescriptor(
            Text(Read(Roles.EnvironmentName)),
            Text(Read(Roles.EnvironmentRegion)),
            Text(Read(Roles.EnvironmentType)),
            new Dictionary<string, string>(Flatten(Read(Roles.EnvironmentValues), string.Empty)),
            Strings(Read(Roles.EnvironmentOffers)),
            Read(Roles.EnvironmentApproval) is JObject block ? ReadApproval(values, block) : null)
        {
            Locations = index.RolesOf(document.Kind).DistinctBy(field => field.Role).ToDictionary(field => field.Role, field => values.Locate(field.Role)),
        };
    }

    /// <summary>
    /// The workload <paramref name="document"/> is, by the <c>workload.*</c> roles (each the first value the document holds, so a role under a list
    /// takes the first item's) and one requirement per <see cref="Roles.Requirement"/> value, with its type, id and class (an absent class binds
    /// its default). Unbound fields are not read, so they are inert.
    /// </summary>
    public Workload ReadWorkload(Document document)
    {
        var values = ValuesOf(document);
        return new Workload(
            Text(values.First(Roles.WorkloadName)),
            Text(values.First(Roles.WorkloadTeam)),
            Text(values.First(Roles.WorkloadImage)),
            (int?)values.First(Roles.WorkloadPort),
            Entries(values.First(Roles.WorkloadVariables)).ToDictionary(variable => variable.Name, variable => Text(variable.Value)),
            [.. values.Every(Roles.Requirement).Select(requirement => ReadRequirement(values, requirement))])
        {
            ImageLocation = values.Locate(Roles.WorkloadImage),
            VariablesLocation = values.Locate(Roles.WorkloadVariables),
            RequirementsLocation = values.LocateList(Roles.Requirement),
        };
    }

    /// <summary>
    /// The policy <paramref name="document"/> is, by the <c>policy.*</c> roles and the criteria of its kind (<see cref="SchemaIndex.CriteriaOf"/>),
    /// each criterion with the value the policy compares, if any. Each field and criterion carries its name for messages, and each field and entry
    /// where it was read.
    /// </summary>
    public Policy ReadPolicy(Document document)
    {
        var values = ValuesOf(document);
        PolicyField Field(string role) => new(values.NameOf(role), values.Locate(role), (string?)values.First(role));
        PolicyEntries EntriesOf(string role) =>
            new(values.NameOf(role), [.. Entries(values.First(role)).Select(entry => new PolicyEntry(entry.Name, entry.Value, entry.Value.Path))]);
        return new Policy
        {
            Source = document.Source,
            Name = Text(values.First(Roles.PolicyName)),
            Reason = Text(values.First(Roles.PolicyReason)),
            Scope = Field(Roles.PolicyScope),
            Runtime = Field(Roles.PolicyRuntime),
            Criteria = [.. index.CriteriaOf(document.Kind).Select(criterion =>
                new Criterion(criterion.Role, criterion.Name ?? string.Empty, (string?)document.Body.SelectToken(criterion.Path)))],
            CriteriaLocation = values.First(Roles.PolicyScope) is { } scope ? Holder(scope).Path : string.Empty,
            Set = EntriesOf(Roles.PolicySet),
            Default = EntriesOf(Roles.PolicyDefault),
            Require = Field(Roles.PolicyRequire),
        };
    }

    /// <summary>The value of <paramref name="role"/> held at the root of <paramref name="document"/>, for a rule on its raw shape; null when it holds none.</summary>
    public JToken? ReadValue(Document document, string role) => ValuesOf(document).Nearest(role, document.Body);

    private Values ValuesOf(Document document) => new(document.Body, index.RolesOf(document.Kind));

    // The count and the flag have no fallback: a default of 0 or false would silently weaken the gate if a binding were lost. Safe to read them
    // so: the schema requires every field of the block when it is present, so a schema-valid document holds both, and the cast throws on a lost one.
    private static ApprovalPolicy ReadApproval(Values values, JObject block) => new(
        Text(values.Nearest(Roles.ApprovalQualifiedOn, block)),
        (int)values.Nearest(Roles.ApprovalRequired, block)!,
        Strings(values.Nearest(Roles.ApprovalApprovers, block)),
        (bool)values.Nearest(Roles.ApprovalSeparate, block)!);

    // The numbers are cast without a fallback, as the approval count: a default would hide a lost binding, and the schema requires every probe and
    // release field of a runtime part, so a schema-valid document holds each inside the part. A part's location is its token's path.
    private static Rollout ReadRollout(Values values, PartOwner runtime, Part part)
    {
        var at = values.Body.SelectToken(part.Location)!;
        string Read(string role) => Text(values.Within(role, at));
        int Number(string role) => (int)values.Within(role, at)!;
        return new Rollout(
            runtime,
            part,
            new Probe(Read(Roles.ProbeScheme), Read(Roles.ProbePath), Number(Roles.ProbeExpectedStatus), Number(Roles.ProbeTimeout), Number(Roles.ProbeInterval)),
            new RuntimeRelease
            {
                SuffixInput = Read(Roles.RolloutSuffixInput),
                TrafficInput = Read(Roles.RolloutTrafficInput),
                TrafficOutput = Read(Roles.RolloutTrafficOutput),
                SuffixOutput = Read(Roles.RolloutSuffixOutput),
                RevisionOutput = Read(Roles.RolloutRevisionOutput),
                FqdnOutput = Read(Roles.RolloutFqdnOutput),
                ResourceIdOutput = Read(Roles.RolloutResourceIdOutput),
                RevisionKey = Read(Roles.RolloutRevisionKey),
                LatestKey = Read(Roles.RolloutLatestKey),
                WeightKey = Read(Roles.RolloutWeightKey),
                Suffix = new RevisionSuffix(Read(Roles.RolloutSuffixPrefix), Number(Roles.RolloutSuffixConfigDigits), Number(Roles.RolloutSuffixSaltDigits)),
                FullWeight = Number(Roles.RolloutFullWeight),
            })
        {
            IntervalLocation = values.Within(Roles.ProbeInterval, at)!.Path,
            TimeoutName = values.NameOf(Roles.ProbeTimeout),
            SuffixMaxLength = Number(Roles.RolloutSuffixMaxLength),
            SuffixMaxLengthLocation = values.Within(Roles.RolloutSuffixMaxLength, at)!.Path,
        };
    }

    // Nested mappings of strings as one value per dot path; the schema refuses a key with a dot, so each dot path is one value (concept 3).
    private static IEnumerable<KeyValuePair<string, string>> Flatten(JToken? mapping, string prefix) =>
        Entries(mapping).SelectMany(entry => entry.Value is JObject
            ? Flatten(entry.Value, $"{prefix}{entry.Name}.")
            : [KeyValuePair.Create(prefix + entry.Name, Text(entry.Value))]);

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
            ExportLocations = Entries(exports).ToDictionary(export => export.Name, export => export.Value.Path),
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

    private static Requirement ReadRequirement(Values values, JToken requirement) => new(
        Text(values.Nearest(Roles.RequirementType, requirement)),
        Text(values.Nearest(Roles.RequirementClass, requirement)))
    {
        DeclaredId = (string?)values.Nearest(Roles.RequirementId, requirement),
        Location = requirement.Path,
    };

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

        // The role's first value inside the object at, for a block the object holds below its own fields (a runtime part's probe); null when none.
        public JToken? Within(string role, JToken at) => Every(role).FirstOrDefault(value => value.Ancestors().Contains(at));

        // A role the document holds once: its first value, so a role under a list takes the first item's.
        public JToken? First(string role) => Every(role).FirstOrDefault();

        // Where the role's first value was read; for a role the document leaves out, the property its field ends in (empty when none).
        public string Locate(string role) => First(role)?.Path ?? NameOf(role);

        // Where the list holding a role on a list's items was read; for a document that holds none of its items, the list's property.
        public string LocateList(string role) => First(role)?.Parent?.Path ?? NameOf(role);

        // The property the role's field ends in, as errors name it (empty when none).
        public string NameOf(string role) => roles.Where(field => field.Role == role).Select(field => field.Name).FirstOrDefault() ?? string.Empty;
    }
}
