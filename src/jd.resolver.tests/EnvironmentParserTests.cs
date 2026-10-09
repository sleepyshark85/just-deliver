using jd.resolver.environment;
using Xunit;

namespace jd.resolver.tests;

public class EnvironmentParserTests
{
    private const string Valid = """
        kind: Environment
        name: test-env
        region: testregion
        tier: team
        values:
          resourceGroup: rg-test
          cosmos: { accountName: acct, accountId: /acct/id, endpoint: "https://acct.example" }
          logAnalytics: { id: /law/id }
        grantable:
          - cosmos.accountId
        """;

    private static Task<EnvironmentLoadResult> Parse(string content) => EnvironmentParser.ParseAsync("env.yaml", content);

    private static async Task<EnvironmentDescriptor> Load(string content)
    {
        var result = await Parse(content);
        Assert.Empty(result.Errors);
        return Assert.IsType<EnvironmentDescriptor>(result.Descriptor);
    }

    [Fact]
    public async Task Valid_descriptor_loads()
    {
        var env = await Load(Valid);
        Assert.Equal("test-env", env.Name);
        Assert.Equal("testregion", env.Region);
        Assert.Equal("team", env.Tier);
        Assert.Equal("rg-test", env.Values["resourceGroup"]);
        Assert.Equal("https://acct.example", env.Values["cosmos.endpoint"]);
        Assert.Equal(["cosmos.accountId"], env.Grantable);
    }

    [Fact]
    public async Task Grantable_is_optional()
    {
        var env = await Load(Valid.Replace("grantable:\n  - cosmos.accountId", string.Empty));
        Assert.Empty(env.Grantable);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("region")]
    [InlineData("tier")]
    public async Task Reserved_key_in_values_is_rejected(string key)
    {
        var result = await Parse(Valid.Replace("  resourceGroup: rg-test", $"  resourceGroup: rg-test\n  {key}: x"));
        var error = Assert.Single(result.Errors);
        Assert.Equal($"values.{key}", error.Location);
        Assert.Null(result.Descriptor);
    }

    [Fact]
    public async Task Grantable_path_missing_from_values_is_rejected()
    {
        var result = await Parse(Valid.Replace("- cosmos.accountId", "- cosmos.missing"));
        var error = Assert.Single(result.Errors);
        Assert.Equal("grantable.0", error.Location);
        Assert.Contains("cosmos.missing", error.Message);
    }

    [Fact]
    public async Task Grantable_path_naming_a_group_rather_than_a_value_is_rejected()
    {
        var result = await Parse(Valid.Replace("- cosmos.accountId", "- cosmos"));
        Assert.Equal("grantable.0", Assert.Single(result.Errors).Location);
    }

    [Fact]
    public async Task Bad_tier_is_rejected_with_its_location()
    {
        var result = await Parse(Valid.Replace("tier: team", "tier: gold"));
        Assert.Null(result.Descriptor);
        Assert.Contains(result.Errors, e => e.Location == "tier" && e.File == "env.yaml");
    }

    [Fact]
    public async Task Code_rule_errors_are_all_reported_together()
    {
        var result = await Parse(Valid.Replace("  resourceGroup: rg-test", "  resourceGroup: rg-test\n  tier: x").Replace("- cosmos.accountId", "- nope"));
        Assert.Equal(["values.tier", "grantable.0"], result.Errors.Select(e => e.Location));
    }

    [Fact]
    public async Task Dotted_key_in_values_is_rejected_so_it_cannot_shadow_a_nested_path()
    {
        var result = await Parse(Valid.Replace("  resourceGroup: rg-test", "  resourceGroup: rg-test\n  cosmos.accountId: /other"));
        var error = Assert.Single(result.Errors);
        Assert.Equal("values.cosmos.accountId", error.Location);
        Assert.Contains("'.'", error.Message);
        Assert.Null(result.Descriptor);
    }

    [Fact]
    public async Task Dotted_key_in_a_nested_mapping_is_rejected()
    {
        var result = await Parse(Valid.Replace("logAnalytics: { id: /law/id }", "logAnalytics: { \"a.b\": x }"));
        Assert.Equal("values.logAnalytics.a.b", Assert.Single(result.Errors).Location);
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
        Assert.True(env.TryGet("tier", out var tier));
        Assert.Equal("team", tier);
    }

    [Fact]
    public async Task Lookup_resolves_nested_values_paths()
    {
        var env = await Load(Valid);
        Assert.True(env.TryGet("resourceGroup", out var group));
        Assert.Equal("rg-test", group);
        Assert.True(env.TryGet("cosmos.accountId", out var id));
        Assert.Equal("/acct/id", id);
        Assert.True(env.TryGet("logAnalytics.id", out var workspace));
        Assert.Equal("/law/id", workspace);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("cosmos.unknown")]
    [InlineData("cosmos")]
    [InlineData("values.resourceGroup")]
    public async Task Lookup_of_an_unknown_path_is_not_found(string path)
    {
        var env = await Load(Valid);
        Assert.False(env.TryGet(path, out _));
    }

    [Fact]
    public async Task Missing_file_is_reported()
    {
        var result = await EnvironmentFile.LoadAsync(Path.Combine(Path.GetTempPath(), "jd-no-such-env.yaml"));
        Assert.Contains("not found", Assert.Single(result.Errors).Message);
    }
}
