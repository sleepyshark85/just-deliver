using jd.definitionvalidator;
using Newtonsoft.Json.Linq;

namespace jd.resolver.environment;

/// <summary>
/// What <c>jd env up</c> provisions: what the <see cref="Offers"/> (component and runtime names) reach in its type's layer. Format:
/// schemas/environment-definition.schema.json.
/// </summary>
public sealed record EnvironmentDefinition(string Name, string Type, IReadOnlyList<string> Offers, ApprovalPolicy? Approval)
{
    /// <summary>The environment the definition is resolved in: its own name, type, offers and approval policy, the given region, and the base descriptor's values.</summary>
    public EnvironmentDescriptor Over(string region, EnvironmentDescriptor? baseDescriptor) =>
        new(Name, region, Type, baseDescriptor?.Values ?? new Dictionary<string, string>(), Offers, Approval);
}

/// <summary><see cref="Definition"/> is set exactly when <see cref="Errors"/> is empty.</summary>
public sealed record EnvironmentDefinitionLoadResult(EnvironmentDefinition? Definition, IReadOnlyList<LoadError> Errors);

/// <summary>Reads an environment definition and validates it against its schema; the catalog's rules are checked when it is resolved.</summary>
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
        return new EnvironmentDefinitionLoadResult(
            new EnvironmentDefinition(
                (string)body["name"]!, (string)body["type"]!, Strings(body["offers"]),
                body["approval"] is { } approval ? ApprovalPolicy.FromJson(approval) : null),
            []);
    }

    internal static List<string> Strings(JToken? token) => token is JArray array ? array.Select(t => (string)t!).ToList() : [];
}
