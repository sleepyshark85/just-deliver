using Xunit;

namespace jd.definitionvalidator.tests;

public class YamlSchemaValidatorTests
{
    private readonly YamlSchemaValidator _validator = new();

    private static readonly string ValidSchema = @"{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""type"": ""object"",
  ""required"": [""name"", ""version""],
  ""properties"": {
    ""name"": {
      ""type"": ""string"",
      ""minLength"": 1
    },
    ""version"": {
      ""type"": ""string"",
      ""pattern"": ""^\\d+\\.\\d+\\.\\d+$""
    },
    ""description"": {
      ""type"": ""string""
    }
  },
  ""additionalProperties"": false
}";

    private static readonly string ValidYaml = @"
name: test-app
version: 1.0.0
description: A test application
";

    private static readonly string InvalidYaml_MissingRequired = @"
name: test-app
description: Missing version field
";

    private static readonly string InvalidYaml_ExtraProperty = @"
name: test-app
version: 1.0.0
extraField: not allowed
";

    private static readonly string InvalidYaml_FailsPattern = @"
name: test-app
version: invalid-version
";

    [Fact]
    public async Task ValidateContentAsync_WithValidYaml_ReturnsSuccess()
    {
        var result = await _validator.ValidateContentAsync(ValidSchema, ValidYaml);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateContentAsync_WithMissingRequired_ReturnsFailed()
    {
        var result = await _validator.ValidateContentAsync(ValidSchema, InvalidYaml_MissingRequired);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task ValidateContentAsync_WithExtraProperty_ReturnsFailed()
    {
        var result = await _validator.ValidateContentAsync(ValidSchema, InvalidYaml_ExtraProperty);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task ValidateContentAsync_WithPatternMismatch_ReturnsFailed()
    {
        var result = await _validator.ValidateContentAsync(ValidSchema, InvalidYaml_FailsPattern);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task ValidateContentAsync_WithInvalidSchema_ReturnsFailed()
    {
        var invalidSchema = "not valid json";
        var result = await _validator.ValidateContentAsync(invalidSchema, ValidYaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task ValidateContentAsync_WithInvalidYaml_ReturnsFailed()
    {
        var invalidYaml = ": invalid yaml :";
        var result = await _validator.ValidateContentAsync(ValidSchema, invalidYaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    // Each input reaches a different narrowed catch: the parser's YamlException (in the parse and in the repeated-key scan), the scanner's
    // InvalidOperationException (the scan), and Newtonsoft's JsonException for an alias that contains itself (the parse).
    [Theory]
    [InlineData(": invalid yaml :", "Validation error: While scanning a plain scalar value, found invalid mapping. (line 1)")]
    [InlineData("a: [1, 2\nb: 3\n", "Validation error: Exception during deserialization (line 1)")]
    [InlineData("a: &x [*x]\n", "Validation error: Self referencing loop detected with type 'System.Collections.Generic.List`1[System.Object]'. Path 'a'.")]
    public async Task Malformed_yaml_is_a_failed_result_naming_what_the_parser_found(string yaml, string error)
    {
        var result = await _validator.ValidateContentAsync(ValidSchema, yaml);

        Assert.False(result.IsValid);
        Assert.Equal(error, Assert.Single(result.Errors));
    }

    // Deeper than the JSON reader's default limit (64), which once turned deep but valid YAML into an exception.
    [Fact]
    public async Task Yaml_nested_deeper_than_64_levels_is_judged_by_the_schema()
    {
        var deep = $"name: test-app\nversion: 1.0.0\ndescription: x\nnested: {string.Concat(Enumerable.Repeat("{a: ", 100))}1{new string('}', 100)}\n";

        var result = await _validator.ValidateContentAsync(ValidSchema, deep);

        Assert.False(result.IsValid);
        Assert.Equal("NoAdditionalPropertiesAllowed: #/nested", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task A_schema_that_is_not_json_is_a_failed_result_naming_the_json_error()
    {
        var result = await _validator.ValidateContentAsync("not valid json", ValidYaml);

        Assert.False(result.IsValid);
        Assert.Equal("Validation error: Unexpected character encountered while parsing value: n. Path '', line 0, position 0.", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task ValidationResult_ToString_FormattsErrors()
    {
        var result = await _validator.ValidateContentAsync(ValidSchema, InvalidYaml_MissingRequired);

        var output = result.ToString();

        Assert.Contains("Validation failed", output);
        Assert.Contains("•", output);
    }

    [Fact]
    public async Task ValidationResult_ToString_FormatSuccess()
    {
        var result = await _validator.ValidateContentAsync(ValidSchema, ValidYaml);

        var output = result.ToString();

        Assert.Equal("Validation passed", output);
    }
}
