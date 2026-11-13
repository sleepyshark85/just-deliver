using Xunit;

namespace jd.resolver.tests;

// Binding completeness (docs/architecture/documents.md section 4): a security boundary reads only what a schema binds, so jd stops when a shipped
// schema does not bind a role one reads. Each test plants one shipped schema with one binding taken out (its keyword turned into a message,
// which binds nothing) beside the other shipped schemas as they are.
public sealed class BindingCompletenessTests
{
    private static string SchemasDirectory => Path.Combine(AppContext.BaseDirectory, "schemas");

    // Every shipped schema under the name jd.resolver.csproj embeds it with, the one named planted with `binding` replaced by `replacement`.
    private static Task<SchemaIndex> PlantedAsync(string schema, string binding, string replacement) => SchemaIndex.BuildAsync([
        .. Directory.EnumerateFiles(SchemasDirectory, "*.schema.json", SearchOption.AllDirectories).Select(file =>
        {
            var name = $"schemas/{Path.GetRelativePath(SchemasDirectory, file).Replace(Path.DirectorySeparatorChar, '/')}";
            var text = File.ReadAllText(file);
            Assert.True(name != schema || text.Contains(binding, StringComparison.Ordinal), $"{schema} has no {binding}");
            return (name, name == schema ? text.Replace(binding, replacement, StringComparison.Ordinal) : text);
        }),
    ]);

    private static async Task<string> UnboundAsync(string schema, string role)
    {
        var index = await PlantedAsync(schema, $"\"is\": \"{role}\"", "\"message\": \"unbound\"");

        return Assert.Throws<InvalidOperationException>(() => index.CheckBindings()).Message;
    }

    [Fact]
    public async Task The_shipped_schemas_bind_every_role_a_boundary_reads()
    {
        var index = await SchemaIndex.Shipped;

        index.CheckBindings();
    }

    // The part schema holds the node roles for every kind with a part, so each kind is named.
    [Theory]
    [InlineData("part.nodes")]
    [InlineData("node.kind")]
    [InlineData("node.template")]
    [InlineData("node.config")]
    public async Task A_kind_with_a_part_that_does_not_bind_a_node_role_stops_jd_naming_each_schema(string role)
    {
        var message = await UnboundAsync("schemas/catalog/part.schema.json", role);

        Assert.Equal(
            $"Schema 'schemas/catalog/component.schema.json' does not bind role '{role}'. Schema 'schemas/catalog/runtime.schema.json' does not bind role '{role}'. "
            + $"Schema 'schemas/catalog/sharedresource.schema.json' does not bind role '{role}'.",
            message);
    }

    // The runtime is the kind that binds the runtime's part, so without it there is none.
    [Fact]
    public async Task A_runtime_that_does_not_bind_its_part_stops_jd()
    {
        var message = await UnboundAsync("schemas/catalog/runtime.schema.json", "runtime.part");

        Assert.Equal("No schema binds role 'runtime.part'.", message);
    }

    [Fact]
    public async Task A_catalog_that_does_not_bind_whether_a_type_is_gated_stops_jd()
    {
        var message = await UnboundAsync("schemas/catalog/catalog.schema.json", "environment-type.gated");

        Assert.Equal("Schema 'schemas/catalog/catalog.schema.json' does not bind role 'environment-type.gated'.", message);
    }

    // Without the layers no part has a deploy order (SchemaIndex.Layers would be empty).
    [Fact]
    public async Task No_schema_binding_the_layers_stops_jd()
    {
        var message = await UnboundAsync("schemas/catalog/part.schema.json", "layer");

        Assert.Equal("No schema binds role 'layer'.", message);
    }
}
