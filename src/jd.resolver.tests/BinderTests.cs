using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.workload;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// The binder knows no kind or field (docs/architecture/documents.md section 3): a planted schema puts the part roles on fields of its own
// names, and the binder fills a part owner from a document of that kind with no C# change. The shipped kinds are bound through the loader
// (CatalogLoaderTests, SeedCatalogTests).
public sealed class BinderTests
{
    private const string Gizmo = """
        "x-jd": { "is": "owner" },
        "properties": {
          "kind": { "enum": ["Gizmo"] },
          "promises": { "type": "array", "items": { "type": "string" }, "x-jd": { "is": "contract" } },
          "needs": { "type": "array", "items": { "type": "string" }, "x-jd": { "is": "part.reads" } },
          "variants": { "type": "object", "x-jd": { "select": "env.type", "fallback": "any" },
                        "additionalProperties": { "$ref": "#/definitions/variant", "x-jd": { "scope": "workload" } } }
        },
        "definitions": { "variant": { "x-jd": { "is": "part" }, "properties": {
          "needs": { "type": "array", "items": { "type": "string" }, "x-jd": { "is": "part.reads" } },
          "bits": { "type": "object", "x-jd": { "is": "part.nodes" }, "additionalProperties": { "properties": {
            "tpl": { "type": "string", "x-jd": { "is": "node.template" } },
            "sort": { "enum": ["create", "grant"], "default": "create", "x-jd": { "is": "node.kind" } },
            "settings": { "type": "object", "x-jd": { "is": "node.config" } } } } },
          "gives": { "type": "object", "x-jd": { "is": "part.exports" } } } } }
        """;

    // The catalog-level roles on fields of its own names: a version, environment types, naming rules, role definitions and grant targets.
    private const string Ledger = """
        "properties": {
          "kind": { "enum": ["Ledger"] },
          "edition": { "type": "string", "x-jd": { "is": "catalog.version" } },
          "stages": { "type": "object", "additionalProperties": { "properties": {
            "tier": { "type": "string", "x-jd": { "is": "environment-type.layer" } },
            "locked": { "type": "boolean", "x-jd": { "is": "environment-type.gated" } } } } },
          "recipes": { "type": "object", "x-jd": { "is": "naming.rules" }, "additionalProperties": { "properties": {
            "shape": { "type": "string", "x-jd": { "is": "naming.pattern" } },
            "longest": { "type": "integer", "x-jd": { "is": "naming.max-length" } },
            "chars": { "type": "string", "x-jd": { "is": "naming.allowed" } } } } },
          "permits": { "type": "object", "x-jd": { "is": "roles" } },
          "aims": { "type": "object", "additionalProperties": { "type": "string", "x-jd": { "is": "grant.target-input" } } }
        }
        """;

    // The workload roles on fields of its own names: a port under a list, a requirement per item, a class with its own default.
    private const string Errand = """
        "properties": {
          "kind": { "enum": ["Errand"] },
          "label": { "type": "string", "x-jd": { "is": "workload.name" } },
          "crew": { "type": "string", "x-jd": { "is": "workload.team" } },
          "box": { "properties": {
            "picture": { "type": "string", "x-jd": { "is": "workload.image" } },
            "settings": { "type": "object", "x-jd": { "is": "workload.variables" } },
            "doors": { "type": "array", "items": { "properties": { "number": { "type": "integer", "x-jd": { "is": "workload.port" } } } } } } },
          "asks": { "type": "array", "items": { "x-jd": { "is": "requirement" }, "properties": {
            "what": { "type": "string", "x-jd": { "is": "requirement.type" } },
            "as": { "type": "string", "x-jd": { "is": "requirement.id" } },
            "grade": { "type": "string", "default": "plain", "x-jd": { "is": "requirement.class" } } } } }
        }
        """;

    private static async Task<Binder> BinderAsync() => new(await SchemaIndex.BuildAsync([
        Shipped("x-jd.schema.json"), Shipped("catalog/part.schema.json"), ("gizmo.json", $$"""{ "$id": "https://test/gizmo.json", {{Gizmo}} }"""),
        ("ledger.json", $$"""{ "$id": "https://test/ledger.json", {{Ledger}} }"""),
        ("errand.json", $$"""{ "$id": "https://test/errand.json", {{Errand}} }"""),
        ("plain.json", """{ "$id": "https://test/plain.json", "properties": { "kind": { "enum": ["Plain"] } } }"""),
    ]));

    private static (string Name, string Text) Shipped(string name) => ($"schemas/{name}", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", name)));

    // A value the part holds is its own (dev's needs); one the owner holds counts for each part that holds none (any's). An absent field binds
    // its schema's default (a's sort); an absent map binds nothing, located at the record that would hold it.
    [Fact]
    public async Task A_planted_kind_binds_its_roles_on_fields_of_its_own_names_into_a_part_owner()
    {
        var document = new Document("g.yaml", "Gizmo", JObject.Parse("""
            { "kind": "Gizmo", "promises": ["out"], "needs": ["base"], "variants": {
                "any": { "bits": { "a": { "tpl": "t/a", "settings": { "k": 1 } } }, "gives": { "out": "${a.x}" } },
                "dev": { "needs": ["own"], "bits": { "b": { "tpl": "t/b", "sort": "grant" } } } } }
            """));

        var owner = (await BinderAsync()).ReadOwner(document, "g");

        Assert.Equal(("g.yaml", "g"), (owner.Source, owner.Name));
        Assert.Equal(["out"], owner.Contract);
        Assert.Equal(["base", "own"], owner.Reads);
        Assert.Equal(["variants.any", "variants.dev"], owner.Parts.Select(part => part.Location));

        var any = owner.Parts[0];
        Assert.Equal("workload", any.Scope);
        Assert.Equal([new SelectKey("env.type", "any", "any", "variants", "variants")], any.Keys);
        Assert.Equal(["base"], any.Reads);
        Assert.Equal("needs", any.ReadsLocation);
        var a = Assert.Single(any.Nodes, node => node.Key == "a").Value;
        Assert.Equal(("t/a", NodeKind.Create, "variants.any.bits.a", "variants.any.bits.a.settings"), (a.Template, a.Kind, a.Location, a.ConfigLocation));
        Assert.Equal(1, (int)Assert.Single(a.Config, entry => entry.Key == "k").Value);
        Assert.Equal(new Dictionary<string, string> { ["out"] = "${a.x}" }, any.Exports);
        Assert.Equal("variants.any.gives", any.ExportsLocation);

        var dev = owner.Parts[1];
        Assert.Equal([new SelectKey("env.type", "dev", "any", "variants", "variants")], dev.Keys);
        Assert.Equal(["own"], dev.Reads);
        Assert.Equal("variants.dev.needs", dev.ReadsLocation);
        var b = Assert.Single(dev.Nodes, node => node.Key == "b").Value;
        Assert.Equal(("t/b", NodeKind.Grant, "variants.dev.bits.b"), (b.Template, b.Kind, b.ConfigLocation));
        Assert.Empty(b.Config);
        Assert.Empty(dev.Exports);
        Assert.Equal("variants.dev", dev.ExportsLocation);
    }

    // A part reading nothing is located at itself, as one without exports is.
    [Fact]
    public async Task A_part_that_reads_nothing_has_its_reads_located_at_the_part()
    {
        var document = new Document("g.yaml", "Gizmo", JObject.Parse("""{ "kind": "Gizmo", "variants": { "any": { "bits": { "a": { "tpl": "t/a" } } } } }"""));

        var part = Assert.Single((await BinderAsync()).ReadOwner(document, "g").Parts);

        Assert.Empty(part.Reads);
        Assert.Equal("variants.any", part.ReadsLocation);
    }

    // A type is each object holding a layer, named by its key; one holding no gate is not gated. A rule holding no maximum or allowed characters
    // binds 0 and is located where the rule is. A kind binding none of the roles binds nothing.
    [Fact]
    public async Task A_planted_kind_binds_the_catalog_level_roles_on_fields_of_its_own_names()
    {
        var binder = await BinderAsync();
        var document = new Document("l.yaml", "Ledger", JObject.Parse("""
            { "kind": "Ledger", "edition": "9",
              "stages": { "early": { "tier": "shared" }, "late": { "tier": "environment", "locked": true } },
              "recipes": { "box": { "shape": "b-{id}", "longest": 12, "chars": "[a-f0-9]" }, "bag": { "shape": "{hash}" } },
              "permits": { "viewer": "0000-1" },
              "aims": { "t/lend": "holder" } }
            """));
        var plain = new Document("p.yaml", "Plain", JObject.Parse("""{ "kind": "Plain" }"""));

        Assert.Equal("9", binder.ReadVersion(document));
        Assert.Equal(
            new Dictionary<string, EnvironmentType> { ["early"] = new(false, "shared"), ["late"] = new(true, "environment") },
            binder.ReadEnvironmentTypes(document));
        var rules = binder.ReadNamingRules(document);
        Assert.Equal(["box", "bag"], rules.Keys);
        Assert.Equal(new NamingRule("b-{id}", 12, "[a-f0-9]") { AllowedLocation = "recipes.box.chars" }, rules["box"]);
        Assert.Equal(new NamingRule("{hash}", 0, string.Empty) { AllowedLocation = "recipes.bag" }, rules["bag"]);
        Assert.Equal(new Dictionary<string, string> { ["viewer"] = "0000-1" }, binder.ReadRoleDefinitions(document));
        Assert.Equal(new Dictionary<string, string> { ["t/lend"] = "holder" }, binder.ReadGrantTargets(document));
        Assert.Equal(string.Empty, binder.ReadVersion(plain));
        Assert.Empty(binder.ReadEnvironmentTypes(plain));
        Assert.Empty(binder.ReadNamingRules(plain));
        Assert.Empty(binder.ReadRoleDefinitions(plain));
        Assert.Empty(binder.ReadGrantTargets(plain));
    }

    // The binder takes only schema-valid documents, which hold both fields of the block; a binding lost from the schema must not read as 0 required
    // approvers and no separation, which would weaken the gate.
    [Theory]
    [InlineData("""{ "qualifiedOn": "dev", "approvers": [], "separateFromDeployer": true }""")] // no count
    [InlineData("""{ "qualifiedOn": "dev", "required": 1, "approvers": [] }""")] // no separation flag
    public async Task An_approval_block_missing_a_field_is_not_bound_to_a_weaker_policy(string approval)
    {
        var index = await SchemaIndex.Shipped;
        var kind = EnvironmentParser.DescriptorKinds(index).Single();
        var body = JObject.Parse($$"""{ "kind": "{{kind}}", "name": "dev", "approval": {{approval}} }""");

        Assert.Throws<ArgumentNullException>(() => new Binder(index).ReadEnvironment(new Document("env.yaml", kind, body)));
    }

    [Fact]
    public async Task A_kind_that_binds_no_owner_is_refused()
    {
        var binder = await BinderAsync();

        var error = Assert.Throws<InvalidOperationException>(() => binder.ReadOwner(new Document("p.yaml", "Plain", JObject.Parse("""{ "kind": "Plain" }""")), "p"));

        Assert.Equal("Kind 'Plain' binds no owner; its documents own no parts.", error.Message);
    }

    // Each workload role is the first value the document holds (a port, the first item's); a requirement without an id has its type as id, and
    // one without a class binds the schema's default.
    [Fact]
    public async Task A_planted_kind_binds_the_workload_roles_on_fields_of_its_own_names()
    {
        var document = new Document("e.yaml", "Errand", JObject.Parse("""
            { "kind": "Errand", "label": "shop", "crew": "team-a",
              "box": { "picture": "reg/app:1", "settings": { "A": "x", "B": "${resource.db.url}" }, "doors": [{ "number": 80 }, { "number": 90 }] },
              "asks": [{ "what": "sqldb", "as": "db", "grade": "big" }, { "what": "cache" }] }
            """));

        var workload = (await BinderAsync()).ReadWorkload(document);

        Assert.Equal(("shop", "team-a", "reg/app:1", 80), (workload.Name, workload.Team, workload.Image, workload.Port));
        Assert.Equal([KeyValuePair.Create("A", "x"), KeyValuePair.Create("B", "${resource.db.url}")], workload.Variables);
        Assert.Equal([new Requirement("sqldb", "big") { DeclaredId = "db", Location = "asks[0]" }, new Requirement("cache", "plain") { Location = "asks[1]" }], workload.Requirements);
        Assert.Equal(["db", "cache"], workload.Requirements.Select(requirement => requirement.Id));
        Assert.Equal(("box.picture", "box.settings"), (workload.ImageLocation, workload.VariablesLocation));
    }

    // An absent image, port, variables or requirements binds nothing; the variables are located by their field's name.
    [Fact]
    public async Task A_workload_that_leaves_out_its_optional_roles_binds_none_of_them()
    {
        var document = new Document("e.yaml", "Errand", JObject.Parse("""{ "kind": "Errand", "label": "shop", "crew": "team-a" }"""));

        var workload = (await BinderAsync()).ReadWorkload(document);

        Assert.Equal(("shop", "team-a", "", null), (workload.Name, workload.Team, workload.Image, workload.Port));
        Assert.Empty(workload.Variables);
        Assert.Empty(workload.Requirements);
        Assert.Equal("settings", workload.VariablesLocation);
    }
}
