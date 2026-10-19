using System.Text;
using jd.resolver.environment;
using jd.resolver.release;
using jd.resolver.workload;
using Xunit;

namespace jd.resolver.tests;

// The one loader (docs/architecture/documents.md section 2, concept 1): the schema comes from the kind, the kinds from the schemas.
public sealed class DocumentLoaderTests : IDisposable
{
    private const string Descriptor = "kind: Environment\nname: dev\nregion: r1\ntype: dev\noffers: []\nvalues: { a: b }\n";

    private readonly string _temp = Directory.CreateTempSubdirectory("jd-document-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_temp, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Every_embedded_schema_declares_its_kinds_and_the_workload_schema_is_one_of_them()
    {
        Assert.Equal(
            ["ApprovalRecord", "Catalog", "Component", "Environment", "EnvironmentDefinition", "Grants", "Naming", "Policy", "ReleaseManifest", "ReleaseRecord",
             "ReleaseSet", "Roles", "Runtime", "SharedResource", "Workload"],
            EmbeddedSchemas.ByKind.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Two_schemas_declaring_the_same_kind_are_refused_naming_both()
    {
        var error = Assert.Throws<InvalidOperationException>(() => EmbeddedSchemas.Index(
            [("a.schema.json", """{ "properties": { "kind": { "enum": ["Thing"] } } }"""), ("b.schema.json", """{ "properties": { "kind": { "enum": ["Other", "Thing"] } } }""")]));

        Assert.Equal("Schemas 'a.schema.json' and 'b.schema.json' both declare kind 'Thing'.", error.Message);
    }

    [Theory]
    [InlineData("""{ "properties": { "name": { "type": "string" } } }""")]
    [InlineData("""{ "properties": { "kind": { "type": "string" } } }""")]
    [InlineData("""{ "properties": { "kind": { "enum": [] } } }""")]
    public void A_schema_declaring_no_kind_is_refused_naming_it(string schema)
    {
        var error = Assert.Throws<InvalidOperationException>(() => EmbeddedSchemas.Index([("a.schema.json", """{ "properties": { "kind": { "enum": ["Thing"] } } }"""), ("b.schema.json", schema)]));

        Assert.Equal("Schema 'b.schema.json' declares no kind: it needs a 'kind' property with an enum.", error.Message);
    }

    [Fact]
    public void Each_declared_kind_maps_to_the_text_of_its_schema()
    {
        const string first = """{ "properties": { "kind": { "enum": ["Thing", "Other"] } } }""";
        const string second = """{ "properties": { "kind": { "enum": ["Third"] } } }""";

        var index = EmbeddedSchemas.Index([("a.schema.json", first), ("b.schema.json", second)]);

        Assert.Equal(new Dictionary<string, string> { ["Thing"] = first, ["Other"] = first, ["Third"] = second }, index);
    }

    [Fact]
    public async Task A_kind_no_schema_declares_is_refused_naming_the_file()
    {
        var result = await DocumentLoader.LoadAsync("odd.yaml", "kind: Widget\nname: x\n", ["Environment"], CancellationToken.None);

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("odd.yaml", "kind", "unknown kind 'Widget'; expected one of Environment."), (error.File, error.Location, error.Message));
    }

    [Fact]
    public async Task A_document_that_is_not_a_mapping_is_refused()
    {
        var result = await DocumentLoader.LoadAsync("list.yaml", "- kind: Environment\n", ["Environment"], CancellationToken.None);

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("list.yaml", string.Empty, "must be one YAML mapping with a 'kind' key."), (error.File, error.Location, error.Message));
    }

    [Theory]
    [InlineData("name: dev\n")]
    [InlineData("kind: 1\n")]
    [InlineData("kind: [Environment]\n")]
    public async Task A_missing_kind_or_one_that_is_not_a_string_is_refused_naming_the_kinds_expected(string text)
    {
        var result = await DocumentLoader.LoadAsync("dev.yaml", text, ["Environment", "Workload"], CancellationToken.None);

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("dev.yaml", "kind", "required; one of Environment, Workload."), (error.File, error.Location, error.Message));
    }

    [Fact]
    public async Task A_document_of_an_accepted_kind_is_validated_by_its_schema()
    {
        var result = await DocumentLoader.LoadAsync("dev.yaml", Descriptor.Replace("region: r1\n", string.Empty, StringComparison.Ordinal), ["Environment"], CancellationToken.None);

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("dev.yaml", "region"), (error.File, error.Location));
        Assert.StartsWith("schema violation: PropertyRequired", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_valid_document_carries_its_source_kind_and_tree()
    {
        var result = await DocumentLoader.LoadAsync("dev.yaml", Descriptor, ["Environment"], CancellationToken.None);

        Assert.Empty(result.Errors);
        Assert.Equal(("dev.yaml", "Environment", "r1"), (result.Value?.Source, result.Value?.Kind, (string?)result.Value?.Body["region"]));
    }

    [Fact]
    public async Task A_descriptor_given_as_a_definition_is_refused()
    {
        var result = await EnvironmentDefinitionFile.LoadAsync(Write("dev.env.yaml", Descriptor));

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("kind", "unknown kind 'Environment'; expected one of EnvironmentDefinition."), (error.Location, error.Message));
    }

    [Fact]
    public async Task A_workload_given_as_a_definition_is_refused()
    {
        var workload = Write("workload.yaml", await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml")));

        var result = await EnvironmentDefinitionFile.LoadAsync(workload);

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal((workload, "kind", "unknown kind 'Workload'; expected one of EnvironmentDefinition."), (error.File, error.Location, error.Message));
    }

    [Fact]
    public async Task A_definition_given_as_a_descriptor_is_refused()
    {
        var result = await EnvironmentParser.ParseAsync("dev.yaml", "kind: EnvironmentDefinition\nname: dev\nregion: r1\ntype: dev\noffers: []\n");

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("kind", "unknown kind 'EnvironmentDefinition'; expected one of Environment."), (error.Location, error.Message));
    }

    [Fact]
    public async Task A_descriptor_given_as_a_workload_is_refused()
    {
        var result = await WorkloadFile.ParseAsync("workload.yaml", Descriptor);

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("kind", "unknown kind 'Environment'; expected one of Workload."), (error.Location, error.Message));
    }

    [Fact]
    public async Task A_manifest_given_as_a_release_set_is_refused()
    {
        var result = await ReleaseSetFile.ParseAsync("set.yaml", Encoding.UTF8.GetBytes("kind: ReleaseManifest\nlabel: v1\nworkloads: []\n"));

        Assert.Null(result.Set);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("kind", "unknown kind 'ReleaseManifest'; expected one of ReleaseSet."), (error.Location, error.Message));
    }

    [Fact]
    public async Task A_release_set_given_as_a_manifest_is_refused()
    {
        var set = Write("set.yaml", "kind: ReleaseSet\nlabel: v1\ncreatedAt: 2026-01-01T00:00:00Z\nworkloads: []\n");

        var result = await ReleaseBuilder.BuildAsync(set, DateTimeOffset.UnixEpoch);

        Assert.Null(result.Set);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("kind", "unknown kind 'ReleaseSet'; expected one of ReleaseManifest."), (error.Location, error.Message));
    }

    [Fact]
    public async Task A_definition_file_given_as_a_descriptor_file_is_refused()
    {
        var definition = Write("dev.env.yaml", "kind: EnvironmentDefinition\nname: dev\nregion: r1\ntype: dev\noffers: []\n");

        var result = await EnvironmentParser.LoadAsync(definition);

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal((definition, "kind", "unknown kind 'EnvironmentDefinition'; expected one of Environment."), (error.File, error.Location, error.Message));
    }

    [Fact]
    public async Task A_descriptor_file_given_as_a_workload_file_is_refused()
    {
        var descriptor = Write("dev.yaml", Descriptor);

        var result = await WorkloadFile.LoadAsync(descriptor);

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal((descriptor, "kind", "unknown kind 'Environment'; expected one of Workload."), (error.File, error.Location, error.Message));
    }

    // Deep but valid YAML is judged by its schema; the JSON reader's own depth limit (64) once escaped the loader as an exception.
    [Fact]
    public async Task A_document_nested_deeper_than_64_levels_is_validated_by_its_schema()
    {
        var result = await EnvironmentParser.ParseAsync("e.yaml", Descriptor + "x: " + new string('[', 80) + new string(']', 80) + "\n");

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("e.yaml", "x"), (error.File, error.Location));
        Assert.StartsWith("schema violation: NoAdditionalPropertiesAllowed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_definition_is_an_error_saying_what_was_not_found()
    {
        var missing = Path.Combine(_temp, "missing.yaml");

        var error = Assert.Single((await EnvironmentDefinitionFile.LoadAsync(missing)).Errors);

        Assert.Equal((missing, string.Empty, "environment definition not found."), (error.File, error.Location, error.Message));
    }
}
