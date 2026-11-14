using jd.resolver.environment;
using jd.resolver.graph;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// Binding completeness (docs/architecture/documents.md section 4): a security boundary reads only what a schema binds, so jd stops when a shipped
// schema does not bind a role one reads. Each test plants one shipped schema with one binding taken out (its keyword turned into a message,
// which binds nothing) beside the other shipped schemas as they are.
public sealed class BindingCompletenessTests
{
    private static string SchemasDirectory => Path.Combine(AppContext.BaseDirectory, "schemas");

    // Every shipped schema under the name jd.resolver.csproj embeds it with, the one named planted with each of `bindings` replaced by `replacement`.
    private static Task<SchemaIndex> PlantedAsync(string schema, string replacement, params string[] bindings) => SchemaIndex.BuildAsync([
        .. Directory.EnumerateFiles(SchemasDirectory, "*.schema.json", SearchOption.AllDirectories).Select(file =>
        {
            var name = $"schemas/{Path.GetRelativePath(SchemasDirectory, file).Replace(Path.DirectorySeparatorChar, '/')}";
            var text = File.ReadAllText(file);
            Assert.True(name != schema || bindings.All(binding => text.Contains(binding, StringComparison.Ordinal)), $"{schema} lacks one of {string.Join(", ", bindings)}");
            return (name, name == schema ? bindings.Aggregate(text, (planted, binding) => planted.Replace(binding, replacement, StringComparison.Ordinal)) : text);
        }),
    ]);

    private static async Task<string> UnboundAsync(string schema, params string[] roles)
    {
        var index = await PlantedAsync(schema, "\"message\": \"unbound\"", [.. roles.Select(role => $"\"is\": \"{role}\"")]);

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

    // The layer is what makes a type's gating readable, so a catalog that binds neither leaves no kind that declares types to check.
    [Fact]
    public async Task A_catalog_that_binds_neither_the_layer_nor_the_gating_of_a_type_stops_jd()
    {
        var message = await UnboundAsync("schemas/catalog/catalog.schema.json", "environment-type.layer", "environment-type.gated");

        Assert.Equal("No schema binds role 'environment-type.layer'.", message);
    }

    // Without the layers no part has a deploy order (SchemaIndex.Layers would be empty).
    [Fact]
    public async Task No_schema_binding_the_layers_stops_jd()
    {
        var message = await UnboundAsync("schemas/catalog/part.schema.json", "layer");

        Assert.Equal("No schema binds role 'layer'.", message);
    }

    // A workload is a kind that binds the workload's name; its variables are workload input into the platform (boundary 6).
    [Fact]
    public async Task A_workload_that_does_not_bind_its_variables_stops_jd()
    {
        var message = await UnboundAsync("schemas/workload.schema.json", "workload.variables");

        Assert.Equal("Schema 'schemas/workload.schema.json' does not bind role 'workload.variables'.", message);
    }

    // The engine reads nothing it has not bound: a field the workload schema accepts but binds to no role cannot carry an expression into the
    // platform, even one reading a deployed part's export. The planted field passes the schema, and the graph is the one without it.
    [Fact]
    public async Task An_unbound_workload_field_carrying_a_part_export_is_inert()
    {
        var index = await PlantedAsync("schemas/workload.schema.json", "\"notes\": { \"type\": \"string\" }, \"variables\": {", "\"variables\": {");
        var sample = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml"));
        var noted = sample.Replace("  variables:\n", "  notes: \"${cosmos-account.id}\"\n  variables:\n", StringComparison.Ordinal);
        Assert.NotEqual(sample, noted);
        var catalog = (await SeedCatalog.LoadAsync()).Value!;
        var environment = (await EnvironmentParser.LoadAsync(await SourceFiles.ReadAsync(Path.Combine(AppContext.BaseDirectory, "golden", "seed-environment.yaml")), CancellationToken.None)).Value!;
        string Resolve(string yaml)
        {
            var document = new Document("workload.yaml", "Workload", (JObject)definitionvalidator.YamlSchemaValidator.ParseYaml(yaml));
            Assert.Empty(index.Validate(document));
            var graph = Resolver.Resolve(new Binder(index).ReadWorkload(document), "workload.yaml", catalog, environment);
            Assert.Empty(graph.Errors);
            return GraphJson.Serialize(graph);
        }

        Assert.Equal(Resolve(sample), Resolve(noted));
    }
}
