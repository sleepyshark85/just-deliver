using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace jd.definitionvalidator;

/// <summary>
/// Rules of the workload format that JSON Schema cannot express. They run on a document that has
/// already passed the schema, so <c>requires</c> and <c>container.variables</c> have the schema's shape.
/// </summary>
public static partial class WorkloadRules
{
    // The whole body of a reference, "<id>.<output>", is captured so malformed ones are reported too.
    [GeneratedRegex(@"\$\{resource\.([^}]*)\}")]
    private static partial Regex ReferenceRegex();

    public static IReadOnlyList<string> Check(JToken workload)
    {
        var errors = new List<string>();
        var requires = workload["requires"] as JArray ?? new JArray();

        // The effective id of a requirement is its id, or its type when no id is set (ADR 0011).
        var declared = new Dictionary<string, List<int>>();
        for (var i = 0; i < requires.Count; i++)
        {
            var effectiveId = (string?)requires[i]["id"] ?? (string?)requires[i]["type"];
            // Unreachable on a schema-valid document (type is required); only satisfies nullability.
            if (effectiveId is null)
            {
                continue;
            }
            if (!declared.TryGetValue(effectiveId, out var positions))
            {
                declared[effectiveId] = positions = new List<int>();
            }
            positions.Add(i);
        }

        foreach (var (effectiveId, positions) in declared.Where(d => d.Value.Count > 1))
        {
            var where = string.Join(", ", positions.Select(p => $"requires[{p}]"));
            errors.Add($"{where}: the id '{effectiveId}' is used more than once (a requirement without an id uses its type as id). Set distinct 'id's.");
        }

        if (workload["container"]?["variables"] is JObject variables)
        {
            foreach (var variable in variables.Properties())
            {
                foreach (Match reference in ReferenceRegex().Matches((string?)variable.Value ?? string.Empty))
                {
                    var body = reference.Groups[1].Value;
                    var separator = body.IndexOf('.');
                    if (separator <= 0 || separator == body.Length - 1)
                    {
                        errors.Add($"container.variables.{variable.Name}: '{reference.Value}' must have the form ${{resource.<id>.<output>}}.");
                        continue;
                    }

                    var id = body[..separator];
                    if (!declared.ContainsKey(id))
                    {
                        var known = declared.Count == 0 ? "none" : string.Join(", ", declared.Keys);
                        errors.Add($"container.variables.{variable.Name}: '{reference.Value}' refers to '{id}', which no requirement declares (declared ids: {known}).");
                    }
                }
            }
        }

        return errors;
    }
}
