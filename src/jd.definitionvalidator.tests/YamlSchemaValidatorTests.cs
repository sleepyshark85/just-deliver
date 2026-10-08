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
