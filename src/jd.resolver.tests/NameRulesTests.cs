using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// The name rules know no namespace, kind or field (docs/architecture/documents.md section 3): a planted meta-schema declares a namespace of its
// own, planted schemas annotate fields with it wherever a subschema can stand, and the rules check documents of those kinds with no C# change.
// The shipped catalog's names are tested through the loader (CatalogParserTests, CatalogUsesTests).
public sealed class NameRulesTests
{
    private const string Kinds = """
        "properties": {
          "kind": { "enum": ["Thing"] },
          "name": { "type": "string", "x-jd": { "define": "widget" } },
          "aliases": { "type": "array", "items": { "type": "string" }, "x-jd": { "define": "widget" } },
          "label": { "type": "string", "x-jd": { "define": ["gadget", "widget"] } },
          "uses": { "type": "array", "items": { "type": "string" }, "x-jd": { "ref": "widget" } },
          "slots": { "type": "object", "x-jd": { "ref": "widget", "select": "env.type", "fallback": "any" } },
          "list": { "type": "array", "items": { "properties": { "of": { "x-jd": { "ref": "widget" } } } } },
          "map": { "type": "object", "additionalProperties": { "properties": { "of": { "x-jd": { "ref": "widget" } } } } },
          "pattern": { "type": "object", "patternProperties": { "^p": { "properties": { "of": { "x-jd": { "ref": "widget" } } } } } },
          "shared": { "$ref": "parts.json#/definitions/of" },
          "all": { "allOf": [{ "$ref": "parts.json#/definitions/of" }] },
          "any": { "anyOf": [{ "$ref": "parts.json#/definitions/of" }] },
          "one": { "oneOf": [{ "$ref": "parts.json#/definitions/of" }] }
        }
        """;

    private static readonly string[] SetKinds = ["Thing", "Single"];

    private static async Task<SchemaIndex> IndexAsync()
    {
        var meta = JObject.Parse(Shipped("x-jd.schema.json"));
        var namespaces = (JArray)meta["definitions"]!["namespace"]!["enum"]!;
        namespaces.Add("widget");
        namespaces.Add("gadget");
        return await SchemaIndex.BuildAsync([
            ("schemas/x-jd.schema.json", meta.ToString()),
            ("schemas/catalog/part.schema.json", Shipped("catalog/part.schema.json")),
            ("thing.json", $$"""{ "$id": "https://test/thing.json", {{Kinds}} }"""),
            ("parts.json", """
                { "$id": "https://test/parts.json", "definitions": {
                  "of": { "properties": { "of": { "x-jd": { "ref": "widget" } } } } } }
                """),
            ("single.json", """{ "$id": "https://test/single.json", "x-jd": { "one": true }, "properties": { "kind": { "enum": ["Single"] } } }"""),
        ]);
    }

    private static string Shipped(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", name));

    private static Document Of(string source, string body) => new(source, (string)JObject.Parse(body)["kind"]!, JObject.Parse(body));

    private static async Task<IReadOnlyList<LoadError>> CheckAsync(params Document[] documents) =>
        NameRules.Check(await IndexAsync(), new DocumentSet("shelf", SetKinds, documents, documents.ToHashSet(), Unreadable: 0));

    [Fact]
    public async Task A_namespace_the_schemas_declare_is_defined_and_referred_to_wherever_a_field_is_annotated()
    {
        var errors = await CheckAsync(
            Of("single.yaml", """{ "kind": "Single" }"""),
            Of("a.yaml", """{ "kind": "Thing", "name": "a" }"""),
            Of("b.yaml", """
                { "kind": "Thing", "name": "b", "uses": ["a", "x"], "slots": { "a": 1, "any": 2, "x": 3 }, "list": [{ "of": "a" }, { "of": "x" }],
                  "map": { "k": { "of": "x" } }, "pattern": { "p1": { "of": "x" } }, "shared": { "of": "x" }, "all": { "of": "x" }, "any": { "of": "x" },
                  "one": { "of": "x" } }
                """));

        const string message = "'x' is not a widget in the shelf (declared: a, b).";
        Assert.Equal(
            [
                new LoadError("b.yaml", "uses", message), new LoadError("b.yaml", "slots.x", message), new LoadError("b.yaml", "list[1].of", message),
                new LoadError("b.yaml", "map.k.of", message), new LoadError("b.yaml", "pattern.p1.of", message), new LoadError("b.yaml", "shared.of", message),
                new LoadError("b.yaml", "all.of", message), new LoadError("b.yaml", "any.of", message), new LoadError("b.yaml", "one.of", message),
            ],
            errors);
    }

    // Only a string, or a list's strings, is a name: these documents are not validated, as a file that failed its schema is not. A field defining
    // in two namespaces clashes in either.
    [Fact]
    public async Task A_name_defined_again_is_an_error_at_each_later_definition_naming_the_first()
    {
        var errors = await CheckAsync(
            Of("single.yaml", """{ "kind": "Single" }"""),
            Of("a.yaml", """{ "kind": "Thing", "name": "a" }"""),
            Of("b.yaml", """{ "kind": "Thing", "name": "a" }"""),
            Of("c.yaml", """{ "kind": "Thing", "name": "a", "aliases": [{}, "a"] }"""),
            Of("d.yaml", """{ "kind": "Thing", "name": 5 }"""),
            Of("e.yaml", """{ "kind": "Thing", "name": "5" }"""),
            Of("f.yaml", """{ "kind": "Thing", "label": "a" }"""));

        Assert.Equal(
            [
                new LoadError("b.yaml", "name", "'a' is already declared in a.yaml."), new LoadError("c.yaml", "name", "'a' is already declared in a.yaml."),
                new LoadError("c.yaml", "aliases", "'a' is already declared in a.yaml."), new LoadError("f.yaml", "label", "'a' is already declared in a.yaml."),
            ],
            errors);
    }

    // The declared names are listed in order, not in the order the files define them.
    [Fact]
    public async Task The_declared_names_of_a_reference_error_are_listed_sorted_whatever_order_they_are_defined_in()
    {
        var errors = await CheckAsync(
            Of("single.yaml", """{ "kind": "Single" }"""),
            Of("a.yaml", """{ "kind": "Thing", "name": "b" }"""),
            Of("b.yaml", """{ "kind": "Thing", "name": "a", "uses": ["x"] }"""));

        Assert.Equal([new LoadError("b.yaml", "uses", "'x' is not a widget in the shelf (declared: a, b).")], errors);
    }

    [Fact]
    public async Task A_one_kind_with_no_document_is_an_error_about_the_set_and_each_extra_document_one_naming_the_first()
    {
        var none = await CheckAsync(Of("a.yaml", """{ "kind": "Thing", "name": "a" }"""));
        var three = await CheckAsync(
            Of("s1.yaml", """{ "kind": "Single" }"""), Of("s2.yaml", """{ "kind": "Single" }"""), Of("s3.yaml", """{ "kind": "Single" }"""));

        Assert.Equal([new LoadError("(shelf)", string.Empty, "no file of kind Single; exactly one is required.")], none);
        Assert.Equal(
            [
                new LoadError("s2.yaml", "kind", "a second Single file; exactly one is allowed (first: s1.yaml)."),
                new LoadError("s3.yaml", "kind", "a second Single file; exactly one is allowed (first: s1.yaml)."),
            ],
            three);
    }
}
