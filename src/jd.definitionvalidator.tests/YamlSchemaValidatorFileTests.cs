using Xunit;
using jd.definitionvalidator;

namespace jd.definitionvalidator.tests;

public class YamlSchemaValidatorFileTests : IAsyncLifetime
{
    private readonly YamlSchemaValidator _validator = new();
    private string _tempDir = string.Empty;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_WithValidFiles_ReturnsSuccess()
    {
        var schemaPath = Path.Combine(_tempDir, "schema.json");
        var yamlPath = Path.Combine(_tempDir, "data.yaml");

        await File.WriteAllTextAsync(schemaPath, ValidSchema);
        await File.WriteAllTextAsync(yamlPath, ValidYaml);

        var result = await _validator.ValidateAsync(schemaPath, yamlPath);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_WithMissingSchemaFile_ReturnsFailed()
    {
        var yamlPath = Path.Combine(_tempDir, "data.yaml");
        await File.WriteAllTextAsync(yamlPath, ValidYaml);

        var result = await _validator.ValidateAsync("/nonexistent/schema.json", yamlPath);

        Assert.False(result.IsValid);
        Assert.Contains("File not found", result.Errors[0]);
    }

    [Fact]
    public async Task ValidateAsync_WithMissingYamlFile_ReturnsFailed()
    {
        var schemaPath = Path.Combine(_tempDir, "schema.json");
        await File.WriteAllTextAsync(schemaPath, ValidSchema);

        var result = await _validator.ValidateAsync(schemaPath, "/nonexistent/data.yaml");

        Assert.False(result.IsValid);
        Assert.Contains("File not found", result.Errors[0]);
    }

    [Fact]
    public async Task ValidateAsync_WithInvalidYamlInFile_ReturnsFailed()
    {
        var schemaPath = Path.Combine(_tempDir, "schema.json");
        var yamlPath = Path.Combine(_tempDir, "data.yaml");

        await File.WriteAllTextAsync(schemaPath, ValidSchema);
        await File.WriteAllTextAsync(yamlPath, InvalidYaml_MissingRequired);

        var result = await _validator.ValidateAsync(schemaPath, yamlPath);

        Assert.False(result.IsValid);
    }

    private static readonly string ValidSchema = @"{
  ""$schema"": ""http://json-schema.org/draft-07/schema#"",
  ""type"": ""object"",
  ""required"": [""name"", ""version""],
  ""properties"": {
    ""name"": { ""type"": ""string"" },
    ""version"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}";

    private static readonly string ValidYaml = @"
name: test-app
version: 1.0.0
";

    private static readonly string InvalidYaml_MissingRequired = @"
name: test-app
";
}
