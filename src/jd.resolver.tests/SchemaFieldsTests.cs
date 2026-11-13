using Xunit;

namespace jd.resolver.tests;

public sealed class SchemaFieldsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("jd-schema-fields-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_fields_are_the_property_names_of_every_schema_at_any_depth()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "catalog"));
        File.WriteAllText(Path.Combine(_directory, "top.schema.json"), """
            {
              "required": ["unlisted"],
              "properties": {
                "kind": { "const": "Top" },
                "nested": { "type": "object", "properties": { "inner": { "type": "string" } } },
                "list": { "type": "array", "items": { "properties": { "item": {} } } },
                "properties": { "type": "object", "properties": { "deeper": {} } }
              },
              "$defs": { "part": { "oneOf": [{ "properties": { "inDefs": {} } }] } }
            }
            """);
        File.WriteAllText(Path.Combine(_directory, "catalog", "other.schema.json"), """{ "properties": { "inSubdirectory": {} } }""");
        File.WriteAllText(Path.Combine(_directory, "notes.json"), """{ "properties": { "notASchema": {} } }""");

        var fields = SchemaFields.In(_directory);

        Assert.Equal(["deeper", "inDefs", "inSubdirectory", "inner", "item", "kind", "list", "nested", "properties"], fields.Order(StringComparer.Ordinal));
    }
}
