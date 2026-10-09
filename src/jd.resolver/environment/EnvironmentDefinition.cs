using jd.definitionvalidator;
using Newtonsoft.Json.Linq;

namespace jd.resolver.environment;

/// <summary>
/// What <c>jd env up</c> provisions: substrate requirements (like a workload's) and the layout of the descriptor written from
/// their outputs. Format: schemas/environment-definition.schema.json. <see cref="Values"/> leaves are expressions.
/// </summary>
public sealed record EnvironmentDefinition(string Name, string Tier, JArray Requires, JObject Values, IReadOnlyList<string> Grantable)
{
    /// <summary>The environment the definition is resolved in: its own name and tier, the given region, and the base descriptor's values.</summary>
    public EnvironmentDescriptor Over(string region, EnvironmentDescriptor? baseDescriptor) =>
        new(Name, region, Tier, baseDescriptor?.Values ?? new Dictionary<string, string>(), baseDescriptor?.Grantable ?? []);
}

/// <summary><see cref="Definition"/> is set exactly when <see cref="Errors"/> is empty.</summary>
public sealed record EnvironmentDefinitionLoadResult(EnvironmentDefinition? Definition, IReadOnlyList<LoadError> Errors);

/// <summary>Reads an environment definition and validates it against its schema and the rule the schema cannot express (unique requirement ids).</summary>
public static class EnvironmentDefinitionFile
{
    private const string SchemaName = "schemas/environment-definition.schema.json";

    private static readonly YamlSchemaValidator Validator = new();

    public static async Task<EnvironmentDefinitionLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new EnvironmentDefinitionLoadResult(null, [new LoadError(path, string.Empty, "environment definition not found.")]);
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        JToken parsed;
        try
        {
            parsed = YamlSchemaValidator.ParseYaml(text);
        }
        catch (Exception ex)
        {
            return new EnvironmentDefinitionLoadResult(null, [new LoadError(path, string.Empty, $"not valid YAML: {YamlSchemaValidator.DescribeYamlError(ex, text)}")]);
        }

        var result = await Validator.ValidateTokenAsync(await EmbeddedSchema.ReadAsync(SchemaName, cancellationToken), parsed);
        if (!result.IsValid || parsed is not JObject body)
        {
            return new EnvironmentDefinitionLoadResult(null, result.Errors.Select(e => LoadError.FromSchema(path, e)).ToList());
        }

        // The schema guarantees these exist and have these shapes.
        var requires = (JArray)body["requires"]!;
        var errors = requires.Select((r, i) => (Id: (string?)r["id"] ?? (string)r["type"]!, Index: i)).GroupBy(r => r.Id).SelectMany(g => g.Skip(1))
            .Select(r => new LoadError(path, $"requires[{r.Index}]", $"requirement id '{r.Id}' is used twice; give one an 'id'."))
            .ToList();
        return errors.Count > 0
            ? new EnvironmentDefinitionLoadResult(null, errors)
            : new EnvironmentDefinitionLoadResult(
                new EnvironmentDefinition(
                    (string)body["name"]!, (string)body["tier"]!, requires, (JObject)body["values"]!, body["grantable"] is JArray g ? g.Select(t => (string)t!).ToList() : []),
                errors);
    }
}
