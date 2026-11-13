using System.Text;
using jd.orchestrator.usecases;
using jd.resolver;
using Xunit;

namespace jd.orchestrator.tests;

// The env up use case over documents only, with a fake backend, probe and templates: every refusal before the walk builds nothing, a failed walk or
// composition writes no descriptor, and the walk is reported node by node.
public sealed class EnvUpTests
{
    private const string DefinitionName = "envs/dev.yaml";
    private const string BaseName = "envs/shared.env.yaml";

    private const string Definition = "kind: EnvironmentDefinition\nname: dev\nregion: region-1\ntype: dev\noffers: [thing]\n";

    private const string Base = "kind: Environment\nname: shared\nregion: region-1\ntype: dev\noffers: []\nvalues: { shared-part: { id: s-1 } }\n";

    // The environment-scope part the offered component uses: two nodes, the second reading the first, so the walk has an order.
    private const string Part =
        "kind: SharedResource\nname: group\ndescription: d\nscope: environment\nnodes:\n  a:\n    template: t/a\n    config: { k: 1 }\n"
        + "  b:\n    template: t/b\n    config: { v: '${a.id}' }\nexports: { id: '${b.id}' }\n";

    private readonly FakeBackend _backend = new();
    private readonly List<Progress> _progress = [];

    private static SourceDocument Document(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    // The catalog with the part, which the offered component uses by the name it is given.
    private static SourceCatalog Catalog(string part = Part, string partName = "group") => new("cat",
    [
        Document("catalog.yaml", "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment }, stage: { layer: environment, approval: true } }\n"),
        Document("shared/group.yaml", part),
        Document("components/thing.yaml",
            $"kind: Component\nname: thing\ndescription: d\nexports: [out]\nuses: [{partName}]\nimplementations:\n  default:\n    classes:\n      standard:\n"
            + "        nodes:\n          n:\n            template: t/n\n            config: { k: 1 }\n        exports:\n          out: x\n"),
        Document("templates/t/a/Pulumi.yaml", "name: a\n"),
    ]);

    private readonly RecordingConnections _connections;

    public EnvUpTests() => _connections = new(_backend);

    private Task<EnvUpResult> UpAsync(string definition = Definition, SourceCatalog? catalog = null, string? baseDescriptor = null, Connections? connections = null) =>
        EnvUp.RunAsync(
            new EnvUpInput(Document(DefinitionName, definition), catalog ?? Catalog(), baseDescriptor is null ? null : Document(BaseName, baseDescriptor)),
            connections ?? _connections.With(), _progress.Add, CancellationToken.None);

    [Fact]
    public async Task Up_walks_the_parts_and_returns_the_descriptor_composed_from_the_outputs_and_the_environment()
    {
        var result = await UpAsync(baseDescriptor: Base);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Empty(result.Errors);
        Assert.Equal(("dev", "region-1", "dev"), (result.Environment?.Name, result.Environment?.Region, result.Environment?.Type));
        Assert.Equal(
            "kind: Environment\nname: dev\nregion: region-1\ntype: dev\noffers:\n- thing\nvalues:\n  shared-part:\n    id: s-1\n  group:\n    id: id-_dev.dev.substrate.b\n",
            Encoding.UTF8.GetString(result.Descriptor!.Value.Span)); // no byte-order mark: GetString would keep one as U+FEFF
        Assert.Equal(["templates cat/templates t/a/Pulumi.yaml", "backend"], _connections.Built);
    }

    [Fact]
    public async Task Up_reports_each_node_as_it_finishes_in_walk_order_and_nothing_else()
    {
        var result = await UpAsync();

        Assert.Equal(["@dev/dev/substrate/a", "@dev/dev/substrate/b"], result.Run?.Nodes.Select(n => n.NodeId));
        Assert.Equal([.. result.Run!.Nodes.Select(n => new NodeFinished(n))], _progress);
    }

    public static TheoryData<string, string?, string, string, string> Refusals => new()
    {
        // what is wrong, the base (null: none), the definition, then the file and location of the first error
        { "the definition is not one", null, "kind: Environment\n", DefinitionName, "kind" },
        { "the base is not one", "kind: EnvironmentDefinition\n", Definition, BaseName, "kind" },
        { "the definition's type is not the catalog's", null, Definition.Replace("type: dev", "type: gold"), DefinitionName, "type" },
        { "the base's type is not the catalog's", Base.Replace("type: dev", "type: gold"), Definition, BaseName, "type" },
        { "a gated type without its block", null, Definition.Replace("type: dev", "type: stage"), DefinitionName, "approval" },
        { "an offer the catalog does not have", null, Definition.Replace("[thing]", "[nothing]"), DefinitionName, "offers[0]" },
        { "a part the base already has", Base.Replace("shared-part", "group"), Definition, DefinitionName, "offers" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refusal_before_the_walk_builds_nothing(string reason, string? baseDescriptor, string definition, string file, string location)
    {
        var result = await UpAsync(definition, baseDescriptor: baseDescriptor);

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal((file, location), (result.Errors[0].File, result.Errors[0].Location));
        Assert.Equal((null, null, null), (result.Run, result.Environment, result.Descriptor));
        Assert.True(_connections.Built.Count == 0, reason);
        Assert.Empty(_backend.Calls);
        Assert.Empty(_progress);
    }

    [Fact]
    public async Task A_catalog_error_names_the_document_under_the_catalog_and_builds_nothing()
    {
        var result = await UpAsync(catalog: Catalog(Part.Replace("description: d\n", string.Empty)));

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal(Path.Combine("cat", "shared/group.yaml"), Assert.Single(result.Errors).File);
        Assert.Null(result.Descriptor);
        Assert.Empty(_connections.Built);
    }

    // A part named like a top-level field resolves, but its exports cannot be values: only the composed descriptor can tell.
    [Fact]
    public async Task A_descriptor_that_cannot_be_composed_before_the_walk_builds_nothing()
    {
        var result = await UpAsync(catalog: Catalog(Part.Replace("name: group", "name: region"), "region"));

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Contains(result.Errors, e => (e.File, e.Location) == (DefinitionName, "values.region"));
        Assert.Equal((null, null), (result.Run, result.Descriptor));
        Assert.Empty(_connections.Built);
    }

    [Fact]
    public async Task A_template_misfit_stops_the_run_before_the_backend_is_built()
    {
        var misfit = new LoadError("@dev/dev/substrate/a", "template", "template 't/a' is not in the template library.");

        var result = await UpAsync(connections: _connections.With(misfit, misfit));

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal([misfit], result.Errors);
        Assert.Equal((null, null), (result.Run, result.Descriptor));
        Assert.Equal(["templates cat/templates t/a/Pulumi.yaml"], _connections.Built);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task A_failed_walk_returns_its_run_and_no_descriptor()
    {
        _backend.FailOn = "_dev.dev.substrate.b";

        var result = await UpAsync();

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Empty(result.Errors);
        Assert.Equal(NodeOutcome.Failed, result.Run?.Nodes[^1].Outcome);
        Assert.Equal("@dev/dev/substrate/b", result.Run?.Nodes[^1].NodeId);
        Assert.Null(result.Descriptor);
        Assert.Equal(result.Run!.Nodes.Count, _progress.Count);
    }

    // The fake backend's key output is a secret: a descriptor value that needs it is never written (security boundary 12).
    [Fact]
    public async Task An_export_that_needs_a_secret_output_fails_after_the_walk_and_returns_no_descriptor()
    {
        var result = await UpAsync(catalog: Catalog(Part.Replace("id: '${b.id}'", "id: '${b.key}'")));

        Assert.Equal(Outcome.Failed, result.Outcome);
        var error = Assert.Single(result.Errors);
        Assert.Equal((DefinitionName, "values.group.id"), (error.File, error.Location));
        Assert.Contains("missing, null or secret", error.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Run);
        Assert.Null(result.Descriptor);
    }
}
