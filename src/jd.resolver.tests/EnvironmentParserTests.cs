using jd.resolver.environment;
using Xunit;

namespace jd.resolver.tests;

public class EnvironmentParserTests
{
    private const string Valid = """
        kind: Environment
        name: test-env
        region: testregion
        type: dev
        offers: [container-app]
        values:
          resourceGroup: rg-test
          cosmos: { accountName: acct, accountId: /acct/id, endpoint: "https://acct.example" }
          logAnalytics: { id: /law/id }
        """;

    private static Task<LoadResult<EnvironmentDescriptor>> Parse(string content) => EnvironmentParser.ParseAsync("env.yaml", content);

    private static async Task<EnvironmentDescriptor> Load(string content)
    {
        var result = await Parse(content);
        Assert.Empty(result.Errors);
        return Assert.IsType<EnvironmentDescriptor>(result.Value);
    }

    [Fact]
    public async Task Valid_descriptor_loads()
    {
        var env = await Load(Valid);
        Assert.Equal("test-env", env.Name);
        Assert.Equal("testregion", env.Region);
        Assert.Equal("dev", env.Type);
        Assert.Equal("rg-test", env.Values["resourceGroup"]);
        Assert.Equal("https://acct.example", env.Values["cosmos.endpoint"]);
        Assert.Equal(["container-app"], env.Offers);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("region")]
    [InlineData("type")]
    public async Task Reserved_key_in_values_is_rejected(string key)
    {
        var result = await Parse(Valid.Replace("  resourceGroup: rg-test", $"  resourceGroup: rg-test\n  {key}: x"));
        var error = Assert.Single(result.Errors);
        Assert.Equal($"values.{key}", error.Location);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Grantable_is_no_longer_a_descriptor_field()
    {
        var result = await Parse(Valid + "\ngrantable: [cosmos.accountId]\n");
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, e => e.Message.Contains("grantable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bad_type_is_rejected_with_its_location()
    {
        var result = await Parse(Valid.Replace("type: dev", "type: Gold"));
        Assert.Null(result.Value);
        Assert.Contains(result.Errors, e => e.Location == "type" && e.File == "env.yaml");
    }

    [Fact]
    public async Task Code_rule_errors_are_all_reported_together()
    {
        var result = await Parse(Valid.Replace("  resourceGroup: rg-test", "  resourceGroup: rg-test\n  type: x\n  region: y"));
        Assert.Equal(["values.region", "values.type"], result.Errors.Select(e => e.Location));
    }

    [Fact]
    public async Task Dotted_key_in_values_is_rejected_so_it_cannot_shadow_a_nested_path()
    {
        var result = await Parse(Valid.Replace("  resourceGroup: rg-test", "  resourceGroup: rg-test\n  cosmos.accountId: /other"));
        var error = Assert.Single(result.Errors);
        Assert.Equal(("values['cosmos.accountId']", DottedKeyMessage), (error.Location, error.Message));
        Assert.Null(result.Value);
    }

    // The schema's message, reported where the key is however deep it is nested (the validator ignores propertyNames, so the rule is
    // patternProperties with a refusing additionalProperties).
    private const string DottedKeyMessage = "keys must be non-empty and must not contain '.'; nest a mapping instead (a: { b: … }).";

    [Theory]
    [InlineData("logAnalytics: { \"a.b\": x }", "values.logAnalytics['a.b']")]
    [InlineData("logAnalytics: { deeper: { \".b\": x } }", "values.logAnalytics.deeper['.b']")]
    [InlineData("logAnalytics: { \"\": x }", "values.logAnalytics.")]
    public async Task A_dotted_or_empty_key_in_a_nested_mapping_is_rejected_with_the_schema_message(string values, string location)
    {
        var result = await Parse(Valid.Replace("logAnalytics: { id: /law/id }", values));

        var error = Assert.Single(result.Errors);
        Assert.Equal((location, DottedKeyMessage), (error.Location, error.Message));
    }

    [Fact]
    public async Task A_leaf_that_is_neither_a_string_nor_a_mapping_reports_the_validators_text_not_the_key_message()
    {
        var result = await Parse(Valid.Replace("logAnalytics: { id: /law/id }", "logAnalytics: { id: [x] }"));

        var error = Assert.Single(result.Errors);
        Assert.Equal("values.logAnalytics", error.Location);
        Assert.StartsWith("schema violation: AdditionalPropertiesNotValid: #/values.logAnalytics", error.Message);
    }

    // A dotted key does not hide a bad leaf beside it (documents.md section 6), and the mapping it sits in is not also reported as a string that
    // is not one: of the failed anyOf, only the branch with the message counts.
    [Fact]
    public async Task A_dotted_key_and_a_bad_leaf_beside_it_are_both_reported()
    {
        var result = await Parse(Valid.Replace("logAnalytics: { id: /law/id }", "logAnalytics: { \"a.b\": x, d: 5 }"));

        const string leaf = "#/values.logAnalytics.d";
        Assert.Equal(
            [
                ("values.logAnalytics.d", $"schema violation: AdditionalPropertiesNotValid: {leaf} {{ NotAnyOf: {leaf} {{ StringExpected: {leaf} }} {{ ObjectExpected: {leaf} }} }}"),
                ("values.logAnalytics['a.b']", DottedKeyMessage),
            ],
            result.Errors.Select(error => (error.Location, error.Message)));
    }

    [Fact]
    public async Task Invalid_yaml_is_reported()
    {
        var result = await Parse("kind: [unclosed");
        Assert.Contains("not valid YAML", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Lookup_resolves_reserved_keys_from_top_level_fields()
    {
        var env = await Load(Valid);
        Assert.True(env.TryGet("name", out var name));
        Assert.Equal("test-env", name);
        Assert.True(env.TryGet("region", out var region));
        Assert.Equal("testregion", region);
        Assert.True(env.TryGet("type", out var type));
        Assert.Equal("dev", type);
    }

    // env.* reads only the top-level fields; a part's exports are read as ${<part>.<export>} (security boundary 4).
    [Theory]
    [InlineData("resourceGroup")]
    [InlineData("cosmos.accountId")]
    [InlineData("unknown")]
    [InlineData("cosmos.unknown")]
    [InlineData("cosmos")]
    [InlineData("values.resourceGroup")]
    public async Task Lookup_of_an_unknown_path_is_not_found(string path)
    {
        var env = await Load(Valid);
        Assert.False(env.TryGet(path, out _));
    }
}
