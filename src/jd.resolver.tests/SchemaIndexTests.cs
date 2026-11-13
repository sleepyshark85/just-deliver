using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// The shipped schemas, built once (docs/architecture/documents.md section 3): checked against the meta-schema, references resolved by $id, the
// kinds from the schemas, and a subschema's x-jd message as the error text (section 6).
public sealed class SchemaIndexTests
{
    private const string Ignored = "the validator (NJsonSchema) ignores this keyword; use enum for a fixed value, patternProperties with additionalProperties for key names, or a rule in code.";

    // The meta-schema refers to the part schema's layer, so a planted set carries both.
    private static readonly (string Name, string Text)[] Meta = [Shipped("x-jd.schema.json"), Shipped("catalog/part.schema.json")];

    private static (string Name, string Text) Shipped(string name) => ($"schemas/{name}", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", name)));

    private static (string Name, string Text) Planted(string name, string body) => (name, $$"""{ "$id": "https://test/{{name}}", {{body}} }""");

    private static Task<SchemaIndex> BuildAsync(params (string Name, string Text)[] planted) => SchemaIndex.BuildAsync([.. Meta, .. planted]);

    private static Document Of(string kind, string body) => new("f.yaml", kind, JObject.Parse(body));

    [Theory]
    [InlineData("ApprovalRecord")]
    [InlineData("Catalog")]
    [InlineData("Component")]
    [InlineData("Environment")]
    [InlineData("EnvironmentDefinition")]
    [InlineData("Grants")]
    [InlineData("Naming")]
    [InlineData("Policy")]
    [InlineData("ReleaseManifest")]
    [InlineData("ReleaseRecord")]
    [InlineData("ReleaseSet")]
    [InlineData("Roles")]
    [InlineData("Runtime")]
    [InlineData("SharedResource")]
    [InlineData("Workload")]
    public async Task The_shipped_schemas_pass_the_meta_schema_and_each_declares_its_kind(string kind)
    {
        var errors = (await SchemaIndex.Shipped).Validate(Of(kind, $$"""{ "kind": "{{kind}}" }"""));

        Assert.NotEmpty(errors);
        Assert.DoesNotContain(errors, error => error.Location == DocumentLoader.KindField);
    }

    [Fact]
    public async Task Two_schemas_declaring_the_same_kind_are_refused_naming_both()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(
            Planted("a.json", """ "properties": { "kind": { "enum": ["Thing"] } } """), Planted("b.json", """ "properties": { "kind": { "enum": ["Other", "Thing"] } } """)));

        Assert.Equal("Schemas 'a.json' and 'b.json' both declare kind 'Thing'.", error.Message);
    }

    [Fact]
    public async Task Two_schemas_with_the_same_id_are_refused_naming_both()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(Planted("same.json", "\"title\": \"a\""), ("b.json", """{ "$id": "https://test/same.json" }""")));

        Assert.Equal("Schemas 'same.json' and 'b.json' both have the $id 'https://test/same.json'.", error.Message);
    }

    [Fact]
    public async Task A_schema_without_an_id_is_refused_naming_it()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(("doc.json", """{ "properties": { "kind": { "enum": ["Thing"] } } }""")));

        Assert.Equal("Schema 'doc.json' has no $id.", error.Message);
    }

    [Fact]
    public async Task Each_declared_kind_is_validated_by_the_schema_that_declares_it()
    {
        var index = await BuildAsync(
            Planted("a.json", """ "required": ["a"], "properties": { "kind": { "enum": ["Thing", "Other"] } } """),
            Planted("b.json", """ "required": ["b"], "properties": { "kind": { "enum": ["Third"] } } """));

        Assert.Equal("a", Assert.Single(index.Validate(Of("Other", """{ "kind": "Other" }"""))).Location);
        Assert.Equal("b", Assert.Single(index.Validate(Of("Third", """{ "kind": "Third" }"""))).Location);
    }

    [Fact]
    public async Task A_schema_declaring_no_kind_is_a_part_another_refers_to_by_its_id()
    {
        var index = await BuildAsync(
            Planted("doc.json", """ "properties": { "kind": { "enum": ["Thing"] }, "node": { "$ref": "part.json#/definitions/node" } } """),
            Planted("part.json", """ "definitions": { "node": { "type": "object", "required": ["template"] } } """));

        var error = Assert.Single(index.Validate(Of("Thing", """{ "kind": "Thing", "node": {} }""")));

        Assert.Equal(("f.yaml", "node.template", "schema violation: PropertyRequired: #/node.template"), (error.File, error.Location, error.Message));
    }

    [Fact]
    public async Task A_reference_to_no_shipped_schema_is_refused_naming_its_id()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(
            Planted("doc.json", """ "properties": { "kind": { "enum": ["Thing"] }, "node": { "$ref": "missing.json" } } """)));

        // The validator wraps the resolver's exception, keeping it as the cause.
        Assert.Equal("No shipped schema has the $id 'https://test/missing.json'.", error.InnerException?.Message);
    }

    [Fact]
    public async Task A_failing_subschema_with_a_message_reports_the_message_at_its_location_and_one_without_reports_the_validators_text()
    {
        var index = await BuildAsync(Planted("doc.json", """
            "properties": {
              "kind": { "enum": ["Thing"] },
              "a": { "type": "string", "pattern": "^x", "x-jd": { "message": "a starts with x." } },
              "b": { "type": "string", "pattern": "^x" } }
            """));

        var errors = index.Validate(Of("Thing", """{ "kind": "Thing", "a": "y", "b": "y" }"""));

        Assert.Equal([new LoadError("f.yaml", "a", "a starts with x."), new LoadError("f.yaml", "b", "schema violation: PatternMismatch: #/b")], errors);
    }

    // Of a failed anyOf or oneOf, the branch carrying a message is the one the value was meant for: its causes are reported, with and without
    // a message, and the other branches failing is not a problem of its own.
    [Theory]
    [InlineData("anyOf")]
    [InlineData("oneOf")]
    public async Task Of_a_failed_choice_only_the_branch_with_a_message_is_reported(string keyword)
    {
        var index = await BuildAsync(Planted("doc.json", $$"""
            "properties": {
              "kind": { "enum": ["Thing"] },
              "a": { "{{keyword}}": [
                { "type": "string" },
                { "type": "object", "required": ["c"], "additionalProperties": { "not": {}, "x-jd": { "message": "no other key." } }, "properties": { "c": {} } }
              ] } }
            """));

        var errors = index.Validate(Of("Thing", """{ "kind": "Thing", "a": { "b": 1 } }"""));

        Assert.Equal([new LoadError("f.yaml", "a.c", "schema violation: PropertyRequired: #/a.c"), new LoadError("f.yaml", "a.b", "no other key.")], errors);
    }

    [Fact]
    public async Task Every_x_jd_keyword_used_as_the_meta_schema_says_is_accepted()
    {
        var index = await BuildAsync(Planted("doc.json", """
            "x-jd": { "is": "owner", "define": ["part owner", "component"], "ref": "component", "select": "env.type", "fallback": "default",
                      "scope": "workload", "match": "requirement.class", "one": true, "message": "m" },
            "properties": { "kind": { "enum": ["Thing"] }, "a": { "x-jd": { "define": "policy", "scope": "shared" } } }
            """));

        Assert.Empty(index.Validate(Of("Thing", """{ "kind": "Thing" }""")));
    }

    // Each keyword the validator ignores is refused wherever a subschema can stand (documents.md section 3).
    [Theory]
    [InlineData("""{ "a": { "if": {} } }""", "properties.a.if")]
    [InlineData("""{ "a": { "then": {} } }""", "properties.a.then")]
    [InlineData("""{ "a": { "else": {} } }""", "properties.a.else")]
    [InlineData("""{ "a": { "const": 1 } }""", "properties.a.const")]
    [InlineData("""{ "a": { "dependencies": {} } }""", "properties.a.dependencies")]
    [InlineData("""{ "a": { "propertyNames": {} } }""", "properties.a.propertyNames")]
    [InlineData("""{ "a": { "contains": {} } }""", "properties.a.contains")]
    [InlineData("""{ "a": { "items": { "const": 1 } } }""", "properties.a.items.const")]
    [InlineData("""{ "a": { "additionalProperties": { "const": 1 } } }""", "properties.a.additionalProperties.const")]
    [InlineData("""{ "a": { "patternProperties": { "^b$": { "const": 1 } } } }""", "properties.a.patternProperties.^b$.const")]
    [InlineData("""{ "a": { "not": { "const": 1 } } }""", "properties.a.not.const")]
    [InlineData("""{ "a": { "allOf": [{ "const": 1 }] } }""", "properties.a.allOf[0].const")]
    [InlineData("""{ "a": { "anyOf": [{ "const": 1 }] } }""", "properties.a.anyOf[0].const")]
    [InlineData("""{ "a": { "oneOf": [{ "const": 1 }] } }""", "properties.a.oneOf[0].const")]
    [InlineData("""{ "a": { "definitions": { "b": { "const": 1 } } } }""", "properties.a.definitions.b.const")]
    public async Task A_keyword_the_validator_ignores_is_refused_at_the_schema_file(string properties, string location)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(Planted("doc.json", $""" "properties": {properties} """)));

        Assert.Equal($"The meta-schema refuses: doc.json: {location}: {Ignored}", error.Message);
    }

    [Theory]
    [InlineData("""{ "iz": "owner" }""", "x-jd.iz", "not an x-jd keyword; the keywords are is, define, ref, select, fallback, scope, match, one and message.")]
    [InlineData("""{ "is": "owner.name" }""", "x-jd.is", "not a role; the roles are the closed list of docs/architecture/documents.md Appendix B.")]
    [InlineData("""{ "select": "env.secret" }""", "x-jd.select", "not a role; the roles are the closed list of docs/architecture/documents.md Appendix B.")]
    [InlineData("""{ "match": "requirement.tier" }""", "x-jd.match", "not a role; the roles are the closed list of docs/architecture/documents.md Appendix B.")]
    [InlineData("""{ "ref": "templates" }""", "x-jd.ref", "not a namespace; the namespaces are listed in docs/architecture/documents.md Appendix B.")]
    [InlineData("""{ "define": "templates" }""", "x-jd.define", "not a namespace; the namespaces are listed in docs/architecture/documents.md Appendix B.")]
    [InlineData("""{ "define": [] }""", "x-jd.define", "not a namespace; the namespaces are listed in docs/architecture/documents.md Appendix B.")]
    public async Task An_unknown_keyword_role_or_namespace_is_refused_with_the_meta_schemas_message(string annotations, string location, string message)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(Planted("doc.json", $$""" "properties": { "a": { "x-jd": {{annotations}} } } """)));

        Assert.Equal($"The meta-schema refuses: doc.json: properties.a.{location}: {message}", error.Message);
    }

    // define takes a namespace or a list of them; the message comes from the namespace each form fails, so a list is refused at the item too.
    [Fact]
    public async Task A_define_list_naming_an_unknown_namespace_is_refused_at_the_keyword_and_the_item()
    {
        const string message = "not a namespace; the namespaces are listed in docs/architecture/documents.md Appendix B.";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(Planted("doc.json", """ "properties": { "a": { "x-jd": { "define": ["component", "templates"] } } } """)));

        Assert.Equal($"The meta-schema refuses: doc.json: properties.a.x-jd.define: {message}; doc.json: properties.a.x-jd.define[1]: {message}", error.Message);
    }

    // A subschema or an x-jd keyword whose value is outside its form: the meta-schema carries no message for these, so the validator's text.
    [Theory]
    [InlineData("5", "ObjectExpected: #/properties.a")]
    [InlineData("""{ "x-jd": "owner" }""", "ObjectExpected: #/properties.a.x-jd")]
    [InlineData("""{ "x-jd": { "scope": "tenant" } }""", "NotAnyOf: #/properties.a.x-jd.scope")]
    [InlineData("""{ "x-jd": { "one": false } }""", "NotInEnumeration: #/properties.a.x-jd.one")]
    [InlineData("""{ "x-jd": { "fallback": "" } }""", "StringTooShort: #/properties.a.x-jd.fallback")]
    [InlineData("""{ "x-jd": { "message": "" } }""", "StringTooShort: #/properties.a.x-jd.message")]
    [InlineData("""{ "x-jd": { "message": 1 } }""", "StringExpected: #/properties.a.x-jd.message")]
    public async Task A_subschema_or_keyword_value_outside_its_form_is_refused_with_the_validators_text(string schema, string violation)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(Planted("doc.json", $$""" "properties": { "a": {{schema}} } """)));

        Assert.StartsWith("The meta-schema refuses: doc.json: properties.a: schema violation: AdditionalPropertiesNotValid: #/properties.a ", error.Message);
        Assert.Contains(violation, error.Message);
    }

    // A recursive schema is walked down to where it repeats (documents.md section 3): an annotation in it would be read at its first level only.
    [Fact]
    public async Task An_annotation_in_a_recursive_schema_is_refused_at_its_path()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(Planted("doc.json", """
            "properties": { "kind": { "enum": ["Thing"] }, "tree": { "$ref": "#/definitions/tree" } },
            "definitions": { "tree": { "properties": {
              "name": { "x-jd": { "define": "component" } }, "children": { "type": "array", "items": { "$ref": "#/definitions/tree" } } } } }
            """)));

        Assert.Equal("The annotation at $['tree']['name'] is in a recursive schema, so it would be read at its first level only.", error.Message);
    }

    // Only an annotation inside the repeating schema is refused: one beside it, and a message (which binds nothing) inside it, are read.
    [Fact]
    public async Task A_recursive_schema_without_annotations_beside_an_annotated_field_is_accepted()
    {
        var index = await BuildAsync(Planted("doc.json", """
            "properties": { "kind": { "enum": ["Thing"] }, "name": { "x-jd": { "define": "component" } }, "tree": { "$ref": "#/definitions/tree" } },
            "definitions": { "tree": { "x-jd": { "message": "m" }, "properties": { "children": { "type": "array", "items": { "$ref": "#/definitions/tree" } } } } }
            """));

        Assert.Equal(["$['name']"], index.NamesOf("Thing").Defines.Select(field => field.Path));
    }

    // Each value schema of a map stands for every key, so two annotated ones would each be read at every value.
    [Theory]
    [InlineData("""{ "patternProperties": { "^a": { "x-jd": { "ref": "component" } }, "^b": { "x-jd": { "ref": "component" } } } }""", 2)]
    [InlineData("""{ "patternProperties": { "^a": { "x-jd": { "ref": "component" } } }, "additionalProperties": { "properties": { "b": { "x-jd": { "is": "part" } } } } }""", 2)]
    public async Task A_map_with_two_annotated_value_schemas_is_refused(string map, int count)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(Planted("doc.json", $$"""
            "properties": { "kind": { "enum": ["Thing"] }, "m": {{map}} }
            """)));

        Assert.Equal($"The map at $['m'] has {count} annotated value schemas; each would be read for every key at $['m'].*.", error.Message);
    }

    [Fact]
    public async Task A_map_with_one_annotated_value_schema_among_several_is_accepted()
    {
        var index = await BuildAsync(Planted("doc.json", """
            "properties": { "kind": { "enum": ["Thing"] }, "m": {
              "patternProperties": { "^a": { "x-jd": { "ref": "component" } }, "^b": { "type": "string" } }, "additionalProperties": { "x-jd": { "message": "m" } } } }
            """));

        Assert.Equal(["$['m'].*"], index.NamesOf("Thing").Refs.Select(field => field.Path));
    }
}
