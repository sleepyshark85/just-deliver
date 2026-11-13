using jd.resolver.catalog;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// The binder knows no kind or field (docs/architecture/documents.md section 3): a planted schema puts the part roles on fields of its own
// names, and the binder fills a part owner from a document of that kind with no C# change. The shipped kinds are bound through the loader
// (CatalogParserTests, SeedCatalogTests).
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

    private static async Task<Binder> BinderAsync() => new(await SchemaIndex.BuildAsync([
        Shipped("x-jd.schema.json"), Shipped("catalog/part.schema.json"), ("gizmo.json", $$"""{ "$id": "https://test/gizmo.json", {{Gizmo}} }"""),
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

        var owner = (await BinderAsync()).Owner(document, "g");

        Assert.Equal(("g.yaml", "g"), (owner.Source, owner.Name));
        Assert.Equal(["out"], owner.Contract);
        Assert.Equal(["base", "own"], owner.Reads);
        Assert.Equal(["variants.any", "variants.dev"], owner.Parts.Select(part => part.Location));

        var any = owner.Parts[0];
        Assert.Equal("workload", any.Scope);
        Assert.Equal([new SelectKey("env.type", "any", "any", "variants", "variants")], any.Keys);
        Assert.Equal(["base"], any.Reads);
        var a = Assert.Single(any.Nodes, node => node.Key == "a").Value;
        Assert.Equal(("t/a", NodeKind.Create, "variants.any.bits.a", "variants.any.bits.a.settings"), (a.Template, a.Kind, a.Location, a.ConfigLocation));
        Assert.Equal(1, (int)Assert.Single(a.Config, entry => entry.Key == "k").Value);
        Assert.Equal(new Dictionary<string, string> { ["out"] = "${a.x}" }, any.Exports);
        Assert.Equal("variants.any.gives", any.ExportsLocation);

        var dev = owner.Parts[1];
        Assert.Equal([new SelectKey("env.type", "dev", "any", "variants", "variants")], dev.Keys);
        Assert.Equal(["own"], dev.Reads);
        var b = Assert.Single(dev.Nodes, node => node.Key == "b").Value;
        Assert.Equal(("t/b", NodeKind.Grant, "variants.dev.bits.b"), (b.Template, b.Kind, b.ConfigLocation));
        Assert.Empty(b.Config);
        Assert.Empty(dev.Exports);
        Assert.Equal("variants.dev", dev.ExportsLocation);
    }

    [Fact]
    public async Task A_kind_that_binds_no_owner_is_refused()
    {
        var binder = await BinderAsync();

        var error = Assert.Throws<InvalidOperationException>(() => binder.Owner(new Document("p.yaml", "Plain", JObject.Parse("""{ "kind": "Plain" }""")), "p"));

        Assert.Equal("Kind 'Plain' binds no owner; its documents own no parts.", error.Message);
    }
}
