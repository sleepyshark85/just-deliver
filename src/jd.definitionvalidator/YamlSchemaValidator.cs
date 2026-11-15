using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace jd.definitionvalidator;

/// <summary>
/// Parses YAML into the JSON tree the schemas are validated against, and validates such a tree against a JSON schema.
/// </summary>
public sealed class YamlSchemaValidator
{
    private const string MultipleDocumentsMessage = "contains several YAML documents; use one YAML document per file";

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        // Without type inference every scalar deserialises as a string, so `port: 8080`
        // arrives as "8080" and any `integer`/`boolean` constraint in the schema fails.
        // Quoted scalars still stay strings.
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
                    case NodeEvent or AnchorAlias:
                        if (open.TryPeek(out var parent) && parent is not null)
                        {
                            if (parent.NextIsKey && parser.Current is Scalar key && !parent.Keys.Add(key.Value))
                            {
                                return (key.Value, key.Start.Line);
                            }

                            parent.NextIsKey = !parent.NextIsKey;
                        }

                        if (parser.Current is MappingStart or SequenceStart)
                        {
                            open.Push(parser.Current is MappingStart ? new MappingFrame() : null);
                        }

                        break;
                    case MappingEnd or SequenceEnd:
                        open.Pop();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException)
        {
            // Best-effort enrichment (the scanner also throws InvalidOperationException, on an unclosed flow collection); the caller falls back to the parser's message.
        }

        return null;
    }

    /// <summary>
    /// Parses YAML into the JSON tree the schemas are validated against (same scalar typing as validation).
    /// Throws <see cref="YamlException"/> on invalid YAML, a repeated key or more than one document, and <see cref="JsonException"/> on an
    /// alias that contains itself.
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

        // Dates stay strings: parsed as DateTime they would be rendered in the machine's time zone, so results would differ by machine.
        // No depth limit: the YAML is read already, so the JSON reader's default limit (64) would only turn deep but valid YAML into a JsonReaderException.
        using var reader = new JsonTextReader(new StringReader(JsonConvert.SerializeObject(yamlObject))) { DateParseHandling = DateParseHandling.None, MaxDepth = null };
        return JToken.Load(reader);
    }

    /// <summary>
    /// Parses as <see cref="ParseYaml"/> does; YAML that does not parse is false, with <paramref name="error"/> describing it as
    /// <see cref="DescribeYamlError"/> does.
    /// </summary>
    public static bool TryParseYaml(string yamlContent, [NotNullWhen(true)] out JToken? tree, [NotNullWhen(false)] out string? error)
    {
        try
        {
            tree = ParseYaml(yamlContent);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is YamlException or JsonException)
        {
            // JsonException: an alias that contains itself cannot become a JSON tree.
            tree = null;
            error = DescribeYamlError(ex, yamlContent);
            return false;
        }
    }

    /// <summary>Validates a document parsed with <see cref="ParseYaml"/> against the schema text; a schema that is not JSON is a failed result.</summary>
    public async Task<ValidationResult> ValidateTokenAsync(string schemaContent, JToken instance, CancellationToken cancellationToken = default)
    {
        JsonSchema schema;
        try
        {
            schema = await JsonSchema.FromJsonAsync(schemaContent, cancellationToken);
        }
        catch (JsonException ex)
        {
            return new ValidationResult([$"Validation error: {ex.Message}"]);
        }

        return new ValidationResult([.. schema.Validate(instance).Select(e => e.ToString() ?? "Validation error")]);
    }
}
