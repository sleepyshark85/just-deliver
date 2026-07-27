using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using YamlDotNet.Serialization;

namespace jd.definitionvalidator;

public class YamlSchemaValidator
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();

    public YamlSchemaValidator()
    {
    }

    public async Task<ValidationResult> ValidateAsync(string schemaFilePath, string yamlFilePath)
    {
        try
        {
            var schemaContent = await File.ReadAllTextAsync(schemaFilePath);
            var yamlContent = await File.ReadAllTextAsync(yamlFilePath);
            return await ValidateContentAsync(schemaContent, yamlContent);
        }
        catch (FileNotFoundException ex)
        {
            return new ValidationResult(false, new[] { $"File not found: {ex.Message}" });
        }
        catch (DirectoryNotFoundException ex)
        {
            return new ValidationResult(false, new[] { $"File not found: {ex.Message}" });
        }
    }

    public async Task<ValidationResult> ValidateContentAsync(string schemaContent, string yamlContent)
    {
        try
        {
            var schema = await JsonSchema.FromJsonAsync(schemaContent);
            var yamlObject = YamlDeserializer.Deserialize(yamlContent);
            var json = JsonConvert.SerializeObject(yamlObject);
            var instance = JToken.Parse(json);

            var validationErrors = schema.Validate(instance);

            if (validationErrors.Count == 0)
            {
                return new ValidationResult(true, Array.Empty<string>());
            }

            var errors = validationErrors
                .Select(e => e.ToString() ?? "Validation error")
                .ToArray();
            return new ValidationResult(false, errors);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, new[] { $"Validation error: {ex.Message}" });
        }
    }
}

public class ValidationResult
{
    public bool IsValid { get; }
    public string[] Errors { get; }

    public ValidationResult(bool isValid, string[] errors)
    {
        IsValid = isValid;
        Errors = errors;
    }

    public override string ToString()
    {
        if (IsValid)
            return "Validation passed";

        var errorLines = string.Join(Environment.NewLine, Errors.Select(e => $"  • {e}"));
        return $"Validation failed:{Environment.NewLine}{errorLines}";
    }
}
