using jd.core.resources;
using Xunit;

namespace jd.core.tests;

public class ResourceTypeTests
{
    private static readonly ResourceTypeCatalog Catalog = ResourceTypeLoader.LoadDefault();

    [Fact]
    public void Catalog_loads_every_type_shipped_with_core()
    {
        Assert.Equal(new[] { "computing", "database", "observability" }, Catalog.Names.Order());
    }

    [Fact]
    public void Rejects_an_unknown_type_and_says_what_is_known()
    {
        var error = Assert.Throws<ArgumentException>(() => Catalog.Get("quantum-ledger"));

        Assert.Contains("Unknown resource type 'quantum-ledger'", error.Message);
        Assert.Contains("database", error.Message);
    }

    // --- properties ---

    [Fact]
    public void Database_exposes_only_a_class_so_no_azure_vocabulary_leaks_in()
    {
        Assert.Equal(new[] { "class" }, Catalog.Get("database").Properties.Keys.Order());
    }

    [Fact]
    public void Classes_default_to_the_cheapest_option()
    {
        Assert.Equal("standard", Catalog.Get("database").Properties["class"].Default);
        Assert.Equal("standard", Catalog.Get("computing").Properties["class"].Default);
        Assert.Equal("standard", Catalog.Get("observability").Properties["class"].Default);
    }

    [Fact]
    public void Only_the_image_is_required_of_a_team()
    {
        Assert.Equal(new[] { "image" }, Catalog.Get("computing").RequiredProperties.Order());
        Assert.Empty(Catalog.Get("database").RequiredProperties);
    }

    [Fact]
    public void Overridable_properties_are_declared_on_the_type()
    {
        Assert.Equal(new[] { "class" }, Catalog.Get("database").OverridableProperties.Order());
        Assert.Equal(new[] { "class", "healthCheckPath" }, Catalog.Get("computing").OverridableProperties.Order());
    }

    [Fact]
    public void Observability_exposes_nothing_a_team_may_override()
    {
        // Retention is a compliance concern; a team lowering it is what policy prevents.
        Assert.Empty(Catalog.Get("observability").OverridableProperties);
    }

    // --- outputs ---

    [Fact]
    public void Outputs_are_declared_with_the_keys_used_in_references()
    {
        Assert.Equal(
            new[] { "connectionString", "databaseName", "endpoint" },
            Catalog.Get("database").Outputs.Keys.Order());
    }

    [Fact]
    public void Credential_outputs_are_marked_and_plain_ones_are_not()
    {
        var database = Catalog.Get("database");
        Assert.True(database.Outputs["connectionString"].Secret);
        Assert.False(database.Outputs["endpoint"].Secret);

        // On Azure this embeds an instrumentation key that grants ingestion.
        Assert.Equal(new[] { "connectionString" }, Catalog.Get("observability").SecretOutputs.Order());
    }

    [Fact]
    public void Every_type_documents_itself_for_generated_reference_docs()
    {
        foreach (var name in Catalog.Names)
        {
            var type = Catalog.Get(name);
            Assert.False(string.IsNullOrWhiteSpace(type.Description), $"'{name}' has no description");
            Assert.All(type.Properties, p =>
                Assert.False(string.IsNullOrWhiteSpace(p.Value.Description), $"'{name}.{p.Key}' has no description"));
            Assert.All(type.Outputs, o =>
                Assert.False(string.IsNullOrWhiteSpace(o.Value.Description), $"'{name}' output '{o.Key}' has no description"));
        }
    }

    // --- loader validation: the guards that replace what the compiler used to do ---

    [Fact]
    public void Rejects_an_enum_with_no_values()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ResourceTypeLoader.Load("""
            name: thing
            properties:
              class: { type: enum }
            """));

        Assert.Contains("enum with no values", error.Message);
    }

    [Fact]
    public void Rejects_a_default_outside_the_enum()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ResourceTypeLoader.Load("""
            name: thing
            properties:
              class: { type: enum, values: [a, b], default: c }
            """));

        Assert.Contains("defaults to 'c'", error.Message);
    }

    [Fact]
    public void Rejects_values_on_a_non_enum_property()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ResourceTypeLoader.Load("""
            name: thing
            properties:
              host: { type: string, values: [a] }
            """));

        Assert.Contains("is not an enum", error.Message);
    }

    [Fact]
    public void Rejects_a_property_that_is_both_required_and_defaulted()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ResourceTypeLoader.Load("""
            name: thing
            properties:
              image: { type: string, required: true, default: nginx }
            """));

        Assert.Contains("required and also has a default", error.Message);
    }

    [Fact]
    public void Rejects_a_duplicate_type_name()
    {
        var definition = ResourceTypeLoader.Load("name: thing");

        var error = Assert.Throws<ArgumentException>(() => new ResourceTypeCatalog(new[] { definition, definition }));

        Assert.Contains("declared more than once", error.Message);
    }
}
