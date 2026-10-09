using Xunit;

namespace jd.definitionvalidator.tests;

// Runs against the real workload schema and sample, plus the workload rules.
public class WorkloadDefinitionTests
{
    private static readonly string SchemaPath = Path.Combine(AppContext.BaseDirectory, "workload.schema.json");
    private static readonly string SamplePath = Path.Combine(AppContext.BaseDirectory, "sample-workload.yaml");

    private readonly YamlSchemaValidator _validator = new(WorkloadRules.Check);

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
    public async Task TwoRequirementsOfSameType_WithDistinctIds_AreValid()
    {
        var result = await ValidateAsync(Workload(
            "  variables:\n    MAIN: ${resource.primary.endpoint}\n    OTHER: ${resource.reporting.endpoint}\n",
            "  - type: database\n    id: primary\n  - type: database\n    id: reporting\n    class: dedicated\n"));

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public async Task ReferenceWithoutId_ResolvesToTypeAsEffectiveId()
    {
        var result = await ValidateAsync(Workload(
            "  variables:\n    DB: ${resource.database.endpoint}\n",
            "  - type: database\n"));

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public async Task TwoRequirementsOfSameTypeWithoutIds_AreRejected_AskingForDistinctIds()
    {
        var result = await ValidateAsync(Workload("", "  - type: database\n  - type: database\n"));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'database'", error);
        Assert.Contains("requires[0], requires[1]", error);
        Assert.Contains("distinct 'id's", error);
    }

    [Fact]
    public async Task ExplicitIdCollidingWithAnotherTypeDefault_IsRejected()
    {
        var result = await ValidateAsync(Workload("", "  - type: database\n  - type: redis\n    id: database\n"));

        Assert.False(result.IsValid);
        Assert.Contains("'database'", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task ReferenceToUndeclaredId_IsRejected_NamingTheVariable()
    {
        var result = await ValidateAsync(Workload(
            "  variables:\n    DB: ${resource.missing.endpoint}\n",
            "  - type: database\n"));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("container.variables.DB", error);
        Assert.Contains("'missing'", error);
        Assert.Contains("database", error);
    }

    [Fact]
    public async Task ReferenceByTypeWhenRequirementHasAnId_IsRejected()
    {
        var result = await ValidateAsync(Workload(
            "  variables:\n    DB: ${resource.database.endpoint}\n",
            "  - type: database\n    id: primary\n"));

        Assert.False(result.IsValid);
        Assert.Contains("'database'", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task ReferenceWithoutOutput_IsRejected()
    {
        var result = await ValidateAsync(Workload(
            "  variables:\n    DB: ${resource.database}\n",
            "  - type: database\n"));

        Assert.False(result.IsValid);
        Assert.Contains("container.variables.DB", Assert.Single(result.Errors));
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
    public async Task WorkloadRulesAreNotRun_WhenSchemaFails()
    {
        // Fails the schema (no type) and would fail the uniqueness rule if the rules ran.
        var result = await ValidateAsync(Workload("", "  - id: dup\n  - id: dup\n"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("type"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("distinct"));
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
