using jd.definitionvalidator;
using jd.resolver.catalog;
using Newtonsoft.Json.Linq;

namespace jd.resolver.environment;

/// <summary>
/// Builds an <see cref="EnvironmentDescriptor"/> from descriptor text. Pure: reading the file is
/// <see cref="EnvironmentFile"/>'s job. Every problem is collected, so a descriptor can be fixed in one pass.
/// </summary>
public static class EnvironmentParser
{
    private static readonly YamlSchemaValidator Validator = new();

    public static async Task<EnvironmentLoadResult> ParseAsync(string source, string content, CancellationToken cancellationToken = default)
    {
        JToken parsed;
        try
        {
            parsed = YamlSchemaValidator.ParseYaml(content);
        }
        catch (Exception ex)
        {
            return new EnvironmentLoadResult(null, [new CatalogError(source, string.Empty, $"not valid YAML: {ex.Message}")]);
        }

        var result = await Validator.ValidateContentAsync(await ReadSchemaAsync(cancellationToken), content);
        if (!result.IsValid || parsed is not JObject body)
        {
            return new EnvironmentLoadResult(null, result.Errors.Select(e => CatalogParser.ToSchemaError(source, e)).ToList());
        }

        // The schema guarantees these exist and have these shapes.
        var valuesNode = (JObject)body["values"]!;
        var errors = new List<CatalogError>();
        foreach (var reserved in EnvironmentDescriptor.ReservedKeys.Where(valuesNode.ContainsKey))
        {
            errors.Add(new CatalogError(source, $"values.{reserved}", $"'{reserved}' is reserved for the top-level field and cannot be a value."));
        }

        var values = new Dictionary<string, string>();
        Flatten(valuesNode, string.Empty, values);
        var grantable = body["grantable"] is JArray array ? array.Select(t => (string?)t ?? string.Empty).ToList() : [];
        foreach (var path in grantable.Where(p => !values.ContainsKey(p)))
        {
            errors.Add(new CatalogError(source, "grantable", $"'{path}' is not a value in 'values'; grantable paths must point at a value."));
        }

        return errors.Count > 0
            ? new EnvironmentLoadResult(null, errors)
            : new EnvironmentLoadResult(
                new EnvironmentDescriptor((string)body["name"]!, (string)body["region"]!, (string)body["tier"]!, values, grantable),
                errors);
    }

    // Values are nested mappings of strings, so every leaf is a string.
    private static void Flatten(JObject node, string prefix, Dictionary<string, string> into)
    {
        foreach (var property in node.Properties())
        {
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value is JObject child)
            {
                Flatten(child, path, into);
            }
            else
            {
                into[path] = (string)property.Value!;
            }
        }
    }

    private static async Task<string> ReadSchemaAsync(CancellationToken cancellationToken)
    {
        const string name = "schemas/environment.schema.json";
        await using var stream = typeof(EnvironmentParser).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Schema '{name}' is not embedded in jd.resolver.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
