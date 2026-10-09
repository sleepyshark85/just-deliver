using jd.definitionvalidator;
using Newtonsoft.Json.Linq;

namespace jd.resolver.workload;

/// <summary><see cref="Workload"/> is set exactly when <see cref="Errors"/> is empty.</summary>
public sealed record WorkloadLoadResult(JObject? Workload, IReadOnlyList<LoadError> Errors);

/// <summary>
/// Reads a workload definition and validates it against the embedded workload schema and rules. The resolver relies on
/// a validated workload (node and stack names are unique by construction only then), so callers load through here.
/// </summary>
public static class WorkloadFile
{
    private static readonly YamlSchemaValidator Validator = new();

    public static async Task<WorkloadLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new WorkloadLoadResult(null, [new LoadError(path, string.Empty, "workload definition not found.")]);
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        JToken parsed;
        try
        {
            parsed = YamlSchemaValidator.ParseYaml(text);
        }
        catch (Exception ex)
        {
            return new WorkloadLoadResult(null, [new LoadError(path, string.Empty, $"not valid YAML: {YamlSchemaValidator.DescribeYamlError(ex, text)}")]);
        }

        var result = await Validator.ValidateTokenAsync(await WorkloadSchema.ReadAsync(cancellationToken), parsed);
        if (!result.IsValid || parsed is not JObject workload)
        {
            // The schema's root "type": "object" means a schema-valid document is an object, so this branch always carries errors.
            return new WorkloadLoadResult(null, result.Errors.Select(e => LoadError.FromSchema(path, e)).ToList());
        }

        // Rule errors read "<location>: <message>".
        var ruleErrors = WorkloadRules.Check(workload).Select(e =>
        {
            var split = e.IndexOf(": ", StringComparison.Ordinal);
            return split < 0 ? new LoadError(path, string.Empty, e) : new LoadError(path, e[..split], e[(split + 2)..]);
        }).ToList();
        return new WorkloadLoadResult(ruleErrors.Count == 0 ? workload : null, ruleErrors);
    }
}
