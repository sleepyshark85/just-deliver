using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using YamlDotNet.Serialization;

namespace jd.definitionvalidator;

public class YamlSchemaValidator
{
    // Without type inference every scalar deserialises as a string, so `port: 8080`
    // arrives as "8080" and any `integer`/`boolean` constraint in the schema fails.
    // Quoted scalars still stay strings.
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        // A repeated key would otherwise silently keep the last value.
        .WithDuplicateKeyChecking()
        .Build();

    private readonly Func<JToken, IReadOnlyList<string>>? _rules;

    /// <summary>Parses YAML into the JSON tree the schemas are validated against (same scalar typing as validation).</summary>
    public static JToken ParseYaml(string yamlContent)
    {
        var yamlObject = YamlDeserializer.Deserialize(yamlContent);
        return JToken.Parse(JsonConvert.SerializeObject(yamlObject));
    }

    /// <param name="rules">
    /// Checks for what the schema cannot express, run only on a document that passed the schema
    /// (for workloads: <see cref="WorkloadRules.Check"/>). Each returned string is one error.
    /// </param>
    public YamlSchemaValidator(Func<JToken, IReadOnlyList<string>>? rules = null)
    {
        _rules = rules;
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
            var instance = ParseYaml(yamlContent);

            var validationErrors = schema.Validate(instance);

            if (validationErrors.Count == 0)
            {
                var ruleErrors = _rules?.Invoke(instance) ?? Array.Empty<string>();
                return ruleErrors.Count == 0
                    ? new ValidationResult(true, Array.Empty<string>())
                    : new ValidationResult(false, ruleErrors.ToArray());
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
