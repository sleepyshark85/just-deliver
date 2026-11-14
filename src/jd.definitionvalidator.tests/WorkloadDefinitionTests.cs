using Xunit;

namespace jd.definitionvalidator.tests;

// Runs against the real workload schema and sample; the workload rules are jd.resolver's (WorkloadRulesTests).
public class WorkloadDefinitionTests
{
    private static readonly string SchemaPath = Path.Combine(AppContext.BaseDirectory, "workload.schema.json");
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "sample-workload.yaml");

    private readonly YamlSchemaValidator _validator = new();

    private const string Header = @"
apiVersion: just-deliver/v1
kind: Workload
metadata:
  name: sample-app
  team: platform-team
";

    private async Task<ValidationResult> ValidateAsync(string yaml) =>
        await _validator.ValidateContentAsync(await File.ReadAllTextAsync(SchemaPath), yaml);

    private static string Workload(string containerExtras, string requires) =>
        Header + "container:\n  image: registry.example/app:1.0.0\n" + containerExtras + "requires:\n" + requires;

    [Fact]
    public async Task MinimalWorkload_IsValid()
    {
        var result = await ValidateAsync(Header + "container:\n  image: registry.example/app:1.0.0\n");

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public async Task SampleWorkload_IsValid()
    {
        var result = await _validator.ValidateAsync(SchemaPath, SamplePath);

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public async Task MetadataEnvironment_IsRejected()
    {
        var result = await ValidateAsync(Header + "  environment: dev\ncontainer:\n  image: registry.example/app:1.0.0\n");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("environment"));
    }

    [Fact]
    public async Task Overrides_AreRejected()
    {
        var result = await ValidateAsync(Workload(
            "",
            "  - type: database\n    overrides:\n      sku: big\n    override_reason: because\n"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("overrides"));
    }

    [Theory]
    [InlineData("type: Cosmos")]
    [InlineData("type: cosmos_sql")]
    [InlineData("type: ab")]
    [InlineData("type: database-too-long-name")]
    [InlineData("type: cosmos-")]
    [InlineData("type: database\n    id: Primary")]
    [InlineData("type: database\n    id: 1primary")]
    [InlineData("type: database\n    id: pp")]
    [InlineData("type: database\n    class: Dedicated")]
    public async Task BadTypeIdOrClassPattern_IsRejected(string requirement)
    {
        var result = await ValidateAsync(Workload("", "  - " + requirement + "\n"));

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task BrokenYaml_IsReportedNotThrown()
    {
        var result = await ValidateAsync(Header + "container: [x\nother: 1\n");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.StartsWith("Validation error:"));
    }

    [Fact]
    public async Task RepeatedKey_IsRejected()
    {
        var result = await ValidateAsync(Header + "container:\n  image: registry.example/app:1.0.0\n  image: other:2\n");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("duplicate key 'image' at line 9."));
    }
}
