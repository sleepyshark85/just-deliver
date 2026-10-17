using System.Security.Cryptography;
using jd.definitionvalidator;
using Newtonsoft.Json.Linq;

namespace jd.resolver.release;

/// <summary>What the two release files share: parsing and schema validation, and the hash that pins a definition.</summary>
internal static class ReleaseDocument
{
    public const string ManifestSchema = "schemas/release-manifest.schema.json";
    public const string SetSchema = "schemas/release-set.schema.json";
    public const string RecordSchema = "schemas/release-record.schema.json";

    private static readonly YamlSchemaValidator Validator = new();

    /// <summary>The body is set exactly when the errors are empty.</summary>
    public static async Task<(JObject? Body, IReadOnlyList<LoadError> Errors)> ParseAsync(string source, string text, string schemaName, CancellationToken cancellationToken)
    {
        JToken parsed;
        try
        {
            parsed = YamlSchemaValidator.ParseYaml(text);
        }
        catch (Exception ex)
        {
            return (null, [new LoadError(source, string.Empty, $"not valid YAML: {YamlSchemaValidator.DescribeYamlError(ex, text)}")]);
        }

        var errors = await ValidateAsync(source, parsed, schemaName, cancellationToken);
        // Both schemas have a root "type": "object", so a schema-valid document is an object and the other branch carries errors.
        return errors.Count == 0 && parsed is JObject body ? (body, []) : (null, errors);
    }

    public static async Task<IReadOnlyList<LoadError>> ValidateAsync(string source, JToken document, string schemaName, CancellationToken cancellationToken)
    {
        var result = await Validator.ValidateTokenAsync(await EmbeddedSchema.ReadAsync(schemaName, cancellationToken), document);
        return result.Errors.Select(e => LoadError.FromSchema(source, e)).ToList();
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
