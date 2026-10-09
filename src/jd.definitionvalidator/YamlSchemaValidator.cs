using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace jd.definitionvalidator;

public class YamlSchemaValidator
{
    // Without type inference every scalar deserialises as a string, so `port: 8080`
    // arrives as "8080" and any `integer`/`boolean` constraint in the schema fails.
    // Quoted scalars still stay strings.
    private const string MultipleDocumentsMessage = "contains several YAML documents; use one YAML document per file";

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        // A repeated key would otherwise silently keep the last value.
        .WithDuplicateKeyChecking()
        .Build();

    /// <summary>
    /// Describes a failure of <see cref="ParseYaml"/> for a person. The parser reports a repeated key at the start of
    /// its mapping and without the key, so that case is located again from the parse events.
    /// </summary>
    public static string DescribeYamlError(Exception error, string yamlContent)
    {
        if (error is not YamlException yaml)
        {
            return error.Message;
        }

        return FindRepeatedKey(yamlContent) is { } repeated
            ? $"duplicate key '{repeated.Key}' at line {repeated.Line}."
            : $"{yaml.Message} (line {yaml.Start.Line})";
    }

    // One frame per open collection: a key set for a mapping (with whether the next node is a key), null for a sequence.
    private sealed class MappingFrame
    {
        public HashSet<string> Keys { get; } = [];

        public bool NextIsKey { get; set; } = true;
    }

    private static (string Key, long Line)? FindRepeatedKey(string yamlContent)
    {
        var open = new Stack<MappingFrame?>();
        try
        {
            var parser = new Parser(new StringReader(yamlContent));
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case NodeEvent node:
                        if (open.TryPeek(out var parent) && parent is not null)
                        {
                            if (parent.NextIsKey && node is Scalar key && !parent.Keys.Add(key.Value))
                            {
                                return (key.Value, key.Start.Line);
                            }

                            parent.NextIsKey = !parent.NextIsKey;
                        }

                        if (node is MappingStart or SequenceStart)
                        {
                            open.Push(node is MappingStart ? new MappingFrame() : null);
                        }

                        break;
                    case MappingEnd or SequenceEnd:
                        open.Pop();
                        break;
                }
            }
        }
        catch (YamlException)
        {
            // Not a repeated key after all; the caller falls back to the parser's own message.
        }

        return null;
    }

    private readonly Func<JToken, IReadOnlyList<string>>? _rules;

    /// <summary>
    /// Parses YAML into the JSON tree the schemas are validated against (same scalar typing as validation).
    /// Throws <see cref="YamlException"/> on invalid YAML, a repeated key or more than one document.
    /// </summary>
    public static JToken ParseYaml(string yamlContent)
    {
        var parser = new Parser(new StringReader(yamlContent));
        // Consuming the stream start ourselves stops the deserializer reading on past the first document.
        parser.Consume<StreamStart>();
        var yamlObject = YamlDeserializer.Deserialize(parser);
        if (parser.Accept<DocumentStart>(out var next))
        {
            throw new YamlException(next.Start, next.End, MultipleDocumentsMessage);
        }

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
        JToken instance;
        try
        {
            instance = ParseYaml(yamlContent);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, new[] { $"Validation error: {DescribeYamlError(ex, yamlContent)}" });
        }

        return await ValidateTokenAsync(schemaContent, instance);
    }

    /// <summary>Validates a document already parsed with <see cref="ParseYaml"/>, so callers that need the tree too parse once.</summary>
    public async Task<ValidationResult> ValidateTokenAsync(string schemaContent, JToken instance)
    {
        try
        {
            var schema = await JsonSchema.FromJsonAsync(schemaContent);
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
