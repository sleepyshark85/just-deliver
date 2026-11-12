using System.Text;
using jd.orchestrator.usecases;
using jd.resolver;
using Xunit;

namespace jd.orchestrator.tests;

// The validate use case over documents only: no file is read, and every error names the document as the input does.
public sealed class ValidateTests
{
    private const string Name = "teams/web/workload.yaml";

    private const string Valid = """
        apiVersion: just-deliver/v1
        kind: Workload
        metadata:
          name: web
          team: team-a
        container:
          image: registry.example/web@sha256:0000000000000000000000000000000000000000000000000000000000000000
        requires:
          - type: database
        """;

    private static Task<ValidateResult> RunAsync(string text) =>
        Validate.RunAsync(new ValidateInput(new SourceDocument(Name, Encoding.UTF8.GetBytes(text))), CancellationToken.None);

    [Fact]
    public async Task A_valid_workload_succeeds_with_no_errors()
    {
        var result = await RunAsync(Valid);

        Assert.Equal(Outcome.Succeeded, result.Outcome);
        Assert.Empty(result.Errors);
    }

    // One row per kind of invalid document: a schema violation, a workload rule, a kind other than Workload, text that is not YAML.
    [Theory]
    [InlineData("name: web", "name: Web_App", "metadata.name", "schema violation: PatternMismatch: #/metadata.name")]
    [InlineData("  - type: database", "  - type: database\n  - type: database", "requires[0], requires[1]",
        "the id 'database' is used more than once (a requirement without an id uses its type as id). Set distinct 'id's.")]
    [InlineData("kind: Workload", "kind: Environment", "kind", "unknown kind 'Environment'; expected one of Workload.")]
    [InlineData("  team: team-a", "  team: team-a\n  team: team-b", "", "not valid YAML: duplicate key 'team' at line 6.")]
    public async Task An_invalid_workload_fails_with_its_exact_error_at_the_name_given(string replaced, string by, string location, string message)
    {
        var result = await RunAsync(Valid.Replace(replaced, by, StringComparison.Ordinal));

        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Equal([new LoadError(Name, location, message)], result.Errors);
    }
}
