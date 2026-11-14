using System.Text;
using jd.resolver.catalog;
using Xunit;

namespace jd.resolver.tests;

// The runtime's rollout data (docs/architecture/documents.md section 9, S24e): the probe scheme, the revision suffix rule and the full traffic weight
// are the runtime's, required at load, bound per implementation, and the suffix's parts must fit its declared longest length.
public sealed class RolloutDataTests
{
    private const string CatalogFile = "kind: Catalog\nversion: \"1\"\nenvironmentTypes: { dev: { layer: environment } }\n";
    private const string Nodes = "nodes:\n  runtime: { template: t/rt, config: {} }\n";
    private const string Suffix = "suffix: { prefix: r, configDigits: 8, saltDigits: 6, maxLength: 20 }";

    private static Task<LoadResult<Catalog>> Load(string runtime) =>
        CatalogLoader.LoadAsync(new SourceCatalog("catalog", [
            new SourceDocument("catalog.yaml", Encoding.UTF8.GetBytes(CatalogFile)),
            new SourceDocument("runtimes/r.yaml", Encoding.UTF8.GetBytes(runtime)),
        ]));

    private static string Runtime(string from, string to) => TestCatalog.Runtime(Nodes).Replace(from, to, StringComparison.Ordinal);

    // A catalog written for an older jd lacks the fields: it fails at load, located at the missing field, not with a default.
    [Theory]
    [InlineData(", scheme: https", "implementations.default.probe.scheme")]
    [InlineData(", " + Suffix, "implementations.default.release.suffix")]
    [InlineData(", fullWeight: 100", "implementations.default.release.fullWeight")]
    public async Task A_runtime_without_a_rollout_field_fails_at_load_naming_the_field(string field, string location)
    {
        var result = await Load(Runtime(field, string.Empty));

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(
            ("runtimes/r.yaml", location, "required: this jd reads it from the runtime; a catalog written for an older jd lacks it, so add it (see catalog/runtimes/container-app.yaml)."),
            (error.File, error.Location, error.Message));
    }

    [Fact]
    public async Task A_suffix_of_21_characters_against_a_max_length_of_20_fails_at_load_naming_the_field()
    {
        var result = await Load(Runtime(Suffix, "suffix: { prefix: rev, configDigits: 12, saltDigits: 6, maxLength: 20 }"));

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(
            ("runtimes/r.yaml", "implementations.default.release.suffix.maxLength", "the suffix's prefix 'rev' and its 12 + 6 digits take 21 characters, more than the 20 allowed."),
            (error.File, error.Location, error.Message));
    }

    [Fact]
    public async Task A_suffix_whose_parts_take_exactly_its_max_length_loads()
    {
        var result = await Load(Runtime(Suffix, "suffix: { prefix: rev, configDigits: 11, saltDigits: 6, maxLength: 20 }"));

        Assert.Empty(result.Errors);
    }

    // Generic bounds only: no Azure or naming number is in the schema or in code.
    [Theory]
    [InlineData("prefix: r,", "prefix: 1r,", "implementations.default.release.suffix.prefix")]
    [InlineData("prefix: r,", "prefix: R,", "implementations.default.release.suffix.prefix")]
    [InlineData("configDigits: 8", "configDigits: 0", "implementations.default.release.suffix.configDigits")]
    [InlineData("saltDigits: 6", "saltDigits: 0", "implementations.default.release.suffix.saltDigits")]
    [InlineData("configDigits: 8", "configDigits: 65", "implementations.default.release.suffix.configDigits")]
    [InlineData("saltDigits: 6", "saltDigits: 65", "implementations.default.release.suffix.saltDigits")]
    [InlineData("maxLength: 20", "maxLength: 0", "implementations.default.release.suffix.maxLength")]
    [InlineData("fullWeight: 100", "fullWeight: 0", "implementations.default.release.fullWeight")]
    [InlineData("scheme: https", "scheme: 'https:'", "implementations.default.probe.scheme")]
    [InlineData("maxLength: 20 }", "maxLength: 20, separator: x }", "implementations.default.release.suffix.separator")]
    public async Task A_rollout_value_out_of_its_generic_bounds_is_a_schema_error(string from, string to, string path)
    {
        var result = await Load(Runtime(from, to));

        var error = Assert.Single(result.Errors);
        Assert.Equal(("runtimes/r.yaml", "implementations.default"), (error.File, error.Location));
        Assert.Contains($"#/{path}", error.Message, StringComparison.Ordinal);
    }

    // Each implementation's rollout is read inside that implementation, not from the first one in the file.
    [Fact]
    public async Task Each_implementation_of_a_runtime_binds_its_own_rollout()
    {
        var other = TestCatalog.RuntimeImplementation(Nodes, "dev")
            .Replace("scheme: https", "scheme: http", StringComparison.Ordinal)
            .Replace(Suffix, "suffix: { prefix: v, configDigits: 4, saltDigits: 3, maxLength: 9 }", StringComparison.Ordinal)
            .Replace("fullWeight: 100", "fullWeight: 50", StringComparison.Ordinal);

        var result = await Load(TestCatalog.Runtime(Nodes) + other);

        Assert.Empty(result.Errors);
        var rollouts = Assert.IsType<Catalog>(result.Value).Rollouts;
        Assert.Equal((TestCatalog.Probe, TestCatalog.Release), rollouts[("container-app", "default")]);
        Assert.Equal(
            (TestCatalog.Probe with { Scheme = "http" }, TestCatalog.Release with { Suffix = new RevisionSuffix("v", 4, 3), FullWeight = 50 }),
            rollouts[("container-app", "dev")]);
    }

    [Fact]
    public async Task A_second_implementations_suffix_too_long_is_located_in_that_implementation()
    {
        var other = TestCatalog.RuntimeImplementation(Nodes, "dev").Replace("maxLength: 20", "maxLength: 14", StringComparison.Ordinal);

        var result = await Load(TestCatalog.Runtime(Nodes) + other);

        Assert.Equal("implementations.dev.release.suffix.maxLength", Assert.Single(result.Errors).Location);
    }
}
