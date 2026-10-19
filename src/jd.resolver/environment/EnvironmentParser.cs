using Newtonsoft.Json.Linq;

namespace jd.resolver.environment;

/// <summary>
/// Builds an <see cref="EnvironmentDescriptor"/> from a descriptor file or text. Every problem is collected, so a descriptor can be fixed in one pass.
/// </summary>
public static class EnvironmentParser
{
    // Bridge: the kind read here until the environment is bound from its schema (S24c1).
    private const string Kind = "Environment";

    /// <summary>Loads the descriptor at <paramref name="path"/>; the value is set exactly when there are no errors.</summary>
    public static async Task<LoadResult<EnvironmentDescriptor>> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Build(await DocumentLoader.LoadFileAsync(path, "environment descriptor", [Kind], cancellationToken));

    /// <summary>Builds a descriptor from its text; <paramref name="source"/> names it in errors.</summary>
    public static async Task<LoadResult<EnvironmentDescriptor>> ParseAsync(string source, string content, CancellationToken cancellationToken = default) =>
        Build(await DocumentLoader.LoadAsync(source, content, [Kind], cancellationToken));

    private static LoadResult<EnvironmentDescriptor> Build(LoadResult<Document> loaded)
    {
        if (loaded.Value is not { Source: var source, Body: var body })
        {
            return new LoadResult<EnvironmentDescriptor>(null, loaded.Errors);
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
        var name = (string)body["name"]!;
        var approval = body["approval"] is { } block ? ApprovalPolicy.FromJson(block) : null;
        errors.AddRange(approval is null ? [] : CheckApproval(approval, name, source));
        return errors.Count > 0
            ? new LoadResult<EnvironmentDescriptor>(null, errors)
            : new LoadResult<EnvironmentDescriptor>(
                new EnvironmentDescriptor(name, (string)body["region"]!, (string)body["type"]!, values, body["offers"]!.ToObject<List<string>>()!, approval), errors);
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
