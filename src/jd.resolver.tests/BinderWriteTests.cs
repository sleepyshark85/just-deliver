using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// The binder writes a document from role values (docs/architecture/documents.md section 3), knowing no kind or field: a planted schema binds
// roles on fields of its own names, in an order of its own, and the binder writes it with no C# change. The shipped kinds are written through
// the descriptor composer (SubstrateTests) and the release set (ReleaseTests), whose goldens are byte-identical.
public sealed class BinderWriteTests
{
    private const string Parcel = """
        "required": ["kind", "tag", "items"],
        "properties": {
          "kind": { "enum": ["Parcel"] },
          "note": { "type": "string" },
          "tag": { "type": "string", "pattern": "^[a-z]+$", "x-jd": { "is": "release.label" } },
          "stamp": { "type": "string", "x-jd": { "is": "release.created-at" } },
          "items": { "type": "array", "items": { "type": "object", "x-jd": { "is": "release.workloads" }, "required": ["body"], "properties": {
            "body": { "type": "string", "x-jd": { "is": "release.definition" } },
            "after": { "type": "array", "items": { "type": "string" }, "x-jd": { "is": "release.depends-on" } } } } },
          "map": { "type": "object", "x-jd": { "is": "env.values" } },
          "gate": { "type": "object", "x-jd": { "is": "env.approval" }, "required": ["count"], "properties": {
            "count": { "type": "integer", "x-jd": { "is": "approval.required" } } } }
        }
        """;

    private static async Task<Binder> BinderAsync() => new(await SchemaIndex.BuildAsync([
        Shipped("x-jd.schema.json"), Shipped("catalog/part.schema.json"), ("parcel.json", $$"""{ "$id": "https://test/parcel.json", {{Parcel}} }"""),
    ]));

    private static (string Name, string Text) Shipped(string name) => ($"schemas/{name}", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schemas", name)));

    private static JObject Item(string body) => new() { [Roles.ReleaseDefinition] = body };

    private static async Task<LoadResult<Document>> WriteAsync(JObject record) => (await BinderAsync()).Write("parcel.yaml", "Parcel", record);

    private static async Task<string> WrittenAsync(JObject record)
    {
        var written = await WriteAsync(record);
        Assert.Empty(written.Errors);
        return written.Value!.Body.ToString(Formatting.None);
    }

    [Fact]
    public async Task The_kind_comes_first_and_each_bound_field_follows_in_the_schemas_property_order()
    {
        var record = new JObject
        {
            [Roles.EnvironmentApproval] = new JObject { [Roles.ApprovalRequired] = 2 },
            [Roles.EnvironmentValues] = new JObject { ["a"] = "1" },
            [Roles.ReleaseWorkloads] = new JArray(Item("b")),
            [Roles.ReleaseCreatedAt] = "now",
            [Roles.ReleaseLabel] = "one",
        };

        Assert.Equal("""{"kind":"Parcel","tag":"one","stamp":"now","items":[{"body":"b"}],"map":{"a":"1"},"gate":{"count":2}}""", await WrittenAsync(record));
    }

    [Fact]
    public async Task A_many_role_writes_one_item_per_record_each_from_its_own_values_in_the_order_given()
    {
        var second = Item("y");
        second[Roles.ReleaseDependsOn] = new JArray("x");
        var record = new JObject { [Roles.ReleaseLabel] = "one", [Roles.ReleaseWorkloads] = new JArray(Item("x"), second) };

        Assert.Equal("""{"kind":"Parcel","tag":"one","items":[{"body":"x"},{"body":"y","after":["x"]}]}""", await WrittenAsync(record));
    }

    [Fact]
    public async Task A_map_keeps_the_order_it_is_given()
    {
        var record = new JObject
        {
            [Roles.ReleaseLabel] = "one",
            [Roles.ReleaseWorkloads] = new JArray(Item("x")),
            [Roles.EnvironmentValues] = new JObject { ["zeta"] = new JObject { ["b"] = "2", ["a"] = "1" }, ["alpha"] = "0" },
        };

        Assert.Equal("""{"kind":"Parcel","tag":"one","items":[{"body":"x"}],"map":{"zeta":{"b":"2","a":"1"},"alpha":"0"}}""", await WrittenAsync(record));
    }

    [Fact]
    public async Task A_null_value_is_not_written()
    {
        var record = new JObject
        {
            [Roles.ReleaseLabel] = "one",
            [Roles.ReleaseCreatedAt] = null,
            [Roles.ReleaseWorkloads] = new JArray(Item("x")),
            [Roles.EnvironmentApproval] = null,
        };

        Assert.Equal("""{"kind":"Parcel","tag":"one","items":[{"body":"x"}]}""", await WrittenAsync(record));
    }

    // The output schema is the allowlist (records are built from an allowlist, conventions): a value under a role the kind does not bind, or under
    // the name of a field the schema leaves unbound, never reaches the document.
    [Fact]
    public async Task A_value_with_no_binding_in_the_schema_is_not_written()
    {
        var record = new JObject
        {
            [Roles.ReleaseLabel] = "one",
            [Roles.ReleaseWorkloads] = new JArray(Item("x")),
            [Roles.WorkloadImage] = "s3cr3t-image",
            ["note"] = "s3cr3t-note",
        };

        Assert.Equal("""{"kind":"Parcel","tag":"one","items":[{"body":"x"}]}""", await WrittenAsync(record));
    }

    [Fact]
    public async Task A_required_bound_field_without_a_value_is_an_error_where_the_document_holds_it_and_nothing_is_written()
    {
        var written = await WriteAsync(new JObject { [Roles.ReleaseWorkloads] = new JArray(Item("x"), new JObject()) });

        Assert.Null(written.Value);
        Assert.Equal(
            [
                new LoadError("parcel.yaml", "tag", "required, but the record holds no value for role 'release.label'."),
                new LoadError("parcel.yaml", "items[1].body", "required, but the record holds no value for role 'release.definition'."),
            ],
            written.Errors);
    }

    [Fact]
    public async Task An_optional_bound_field_without_a_value_is_no_error()
    {
        var written = await WriteAsync(new JObject { [Roles.ReleaseLabel] = "one", [Roles.ReleaseWorkloads] = new JArray(Item("x")) });

        Assert.Empty(written.Errors);
    }

    [Fact]
    public async Task The_written_document_is_validated_against_the_schema_and_not_returned_when_it_fails()
    {
        var written = await WriteAsync(new JObject { [Roles.ReleaseLabel] = "One", [Roles.ReleaseWorkloads] = new JArray(Item("x")) });

        Assert.Null(written.Value);
        Assert.Equal([new LoadError("parcel.yaml", "tag", "schema violation: PatternMismatch: #/tag")], written.Errors);
    }
}
