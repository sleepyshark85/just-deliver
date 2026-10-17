using jd.definitionvalidator;
using Newtonsoft.Json.Linq;

namespace jd.resolver.environment;

/// <summary>
/// Builds an <see cref="EnvironmentDescriptor"/> from descriptor text. Pure: reading the file is
/// <see cref="EnvironmentFile"/>'s job. Every problem is collected, so a descriptor can be fixed in one pass.
/// </summary>
public static class EnvironmentParser
{
    private const string SchemaName = "schemas/environment.schema.json";

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
            return new EnvironmentLoadResult(null, [new LoadError(source, string.Empty, $"not valid YAML: {YamlSchemaValidator.DescribeYamlError(ex, content)}")]);
        }

        var result = await Validator.ValidateTokenAsync(await EmbeddedSchema.ReadAsync(SchemaName, cancellationToken), parsed);
        if (!result.IsValid || parsed is not JObject body)
        {
            return new EnvironmentLoadResult(null, result.Errors.Select(e => LoadError.FromSchema(source, e)).ToList());
        }

        // The schema guarantees these exist and have these shapes.
        var valuesNode = (JObject)body["values"]!;
        var errors = new List<LoadError>();
        foreach (var reserved in EnvironmentDescriptor.ReservedKeys.Where(valuesNode.ContainsKey))
        {
            errors.Add(new LoadError(source, $"values.{reserved}", $"'{reserved}' is reserved for the top-level field and cannot be a value."));
        }

        var values = new Dictionary<string, string>();
        Flatten(valuesNode, string.Empty, values, source, errors);
        var grantable = body["grantable"] is JArray array ? array.Select(t => (string?)t ?? string.Empty).ToList() : [];
        for (var i = 0; i < grantable.Count; i++)
        {
            if (values.ContainsKey(grantable[i]))
            {
                continue;
            }

            errors.Add(new LoadError(source, $"grantable.{i}", $"'{grantable[i]}' is not a value in 'values'; grantable paths must point at a value."));
        }

        var name = (string)body["name"]!;
        var approval = body["approval"] is { } block ? ApprovalPolicy.FromJson(block) : null;
        errors.AddRange(approval is null ? [] : CheckApproval(approval, name, source));
        return errors.Count > 0
            ? new EnvironmentLoadResult(null, errors)
            : new EnvironmentLoadResult(new EnvironmentDescriptor(name, (string)body["region"]!, (string)body["tier"]!, values, grantable, approval), errors);
    }

    // What the schema cannot say: the policy must refer to another environment and be satisfiable.
    private static IEnumerable<LoadError> CheckApproval(ApprovalPolicy approval, string environment, string source)
    {
        if (approval.QualifiedOn == environment)
        {
            yield return new LoadError(source, "approval.qualifiedOn", $"'{environment}' cannot be qualified on itself; name the environment whose records must show the set qualified.");
        }

        if (approval.Required > approval.Approvers.Count)
        {
            yield return new LoadError(source, "approval.required", $"{approval.Required} approvers are required but only {approval.Approvers.Count} are listed; the policy could never be met.");
        }
    }

    // Values are nested mappings of strings, so every leaf is a string. A dot in a key would make a dot path
    // ambiguous (a flat "a.b" key against nested a: { b }), which would let one value shadow another, so it is rejected.
    private static void Flatten(JObject node, string prefix, Dictionary<string, string> into, string source, List<LoadError> errors)
    {
        foreach (var property in node.Properties())
        {
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            if (property.Name.Length == 0 || property.Name.Contains('.'))
            {
                errors.Add(new LoadError(source, $"values.{path}", "keys must be non-empty and must not contain '.'; nest a mapping instead (a: { b: … })."));
            }
            else if (property.Value is JObject child)
            {
                Flatten(child, path, into, source, errors);
            }
            else
            {
                into[path] = (string)property.Value!;
            }
        }
    }
}
