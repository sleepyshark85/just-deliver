using jd.resolver.workload;
using Xunit;

namespace jd.resolver.tests;

// The workload rules (WorkloadRules) through the loader, on a definition that passes its schema: effective ids unique (ADR 0011), and a variable
// reads only a requirement's export (boundary 6). Moved from jd.definitionvalidator.tests with the rules; each error reads "file: location: message".
public sealed class WorkloadRulesTests
{
    private const string Header = @"
apiVersion: just-deliver/v1
kind: Workload
metadata:
  name: sample-app
  team: platform-team
";

    private static async Task<(bool IsValid, IReadOnlyList<string> Errors)> ValidateAsync(string yaml)
    {
        var result = await WorkloadFile.ParseAsync("workload.yaml", yaml);
        return (result.Value is not null, [.. result.Errors.Select(error => error.ToString())]);
    }

    private static string Workload(string containerExtras, string requires) =>
        Header + "container:\n  image: registry.example/app:1.0.0\n" + containerExtras + "requires:\n" + requires;

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

    [Theory]
    [InlineData("${env.cosmos.accountId}")]
    [InlineData("${cosmos-account.id}")]                   // a deployed part's export: platform-side, never a workload input (boundary 6)
    [InlineData("${name('ca')}")]
    [InlineData("${workload.team}")]
    [InlineData("${runtime.principalId}")]
    [InlineData("${role.cosmos-data-contributor}")]
    [InlineData("${appinsights.connectionString}")]
    [InlineData("ok-${resource.database.endpoint}-${env.name}")]
    public async Task AVariableMayOnlyReferenceARequirementsOutput(string value)
    {
        var result = await ValidateAsync(Workload(
            $"  variables:\n    BAD: \"{value}\"\n",
            "  - type: database\n"));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("container.variables.BAD", error);
        Assert.Contains("${resource.<id>.<output>}", error);
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

    [Theory]
    [InlineData("${resource.database}")]
    [InlineData("${resource.database.}")]
    [InlineData("${resource..endpoint}")]
    public async Task A_reference_without_an_id_and_an_output_is_refused_with_the_form(string value)
    {
        var result = await ValidateAsync(Workload($"  variables:\n    DB: \"{value}\"\n", "  - type: database\n"));

        Assert.Equal([$"workload.yaml: container.variables.DB: '{value}' must have the form ${{resource.<id>.<output>}}."], result.Errors);
    }

    [Fact]
    public async Task A_reference_to_an_undeclared_id_names_the_declared_ids_or_none()
    {
        var none = await ValidateAsync(Workload("  variables:\n    DB: ${resource.missing.endpoint}\n", "  []\n"));
        var two = await ValidateAsync(Workload("  variables:\n    DB: ${resource.missing.endpoint}\n", "  - type: database\n  - type: cache\n    id: hot\n"));

        Assert.Equal(["workload.yaml: container.variables.DB: '${resource.missing.endpoint}' refers to 'missing', which no requirement declares (declared ids: none)."], none.Errors);
        Assert.Equal(["workload.yaml: container.variables.DB: '${resource.missing.endpoint}' refers to 'missing', which no requirement declares (declared ids: database, hot)."], two.Errors);
    }

    [Fact]
    public async Task A_reference_outside_resource_is_refused_with_the_one_form_allowed()
    {
        var result = await ValidateAsync(Workload("  variables:\n    BAD: ${cosmos-account.id}\n", "  - type: database\n"));

        Assert.Equal(["workload.yaml: container.variables.BAD: '${cosmos-account.id}' is not allowed; a variable may only reference a requirement's output, as ${resource.<id>.<output>}."], result.Errors);
    }

    // Every rule is checked: an id used twice and each bad variable, the ids first, the variables in the order written.
    [Fact]
    public async Task Every_broken_rule_is_reported_in_one_pass()
    {
        var result = await ValidateAsync(Workload(
            "  variables:\n    A: ${env.name}\n    B: ${resource.database.endpoint} ${resource.nope.x}\n",
            "  - type: database\n  - type: cache\n    id: database\n"));

        Assert.Equal(
        [
            "workload.yaml: requires[0], requires[1]: the id 'database' is used more than once (a requirement without an id uses its type as id). Set distinct 'id's.",
            "workload.yaml: container.variables.A: '${env.name}' is not allowed; a variable may only reference a requirement's output, as ${resource.<id>.<output>}.",
            "workload.yaml: container.variables.B: '${resource.nope.x}' refers to 'nope', which no requirement declares (declared ids: database).",
        ], result.Errors);
    }
}
