using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expressions;
using jd.resolver.matching;
using Newtonsoft.Json.Linq;

namespace jd.resolver.expansion;

/// <summary>
/// Turns each requirement of a validated workload into the nodes of its selected mapping, with config and exports
/// evaluated. Pure; the catalog is never modified. Rules: docs/architecture/resolver.md.
/// </summary>
public sealed class Expander(Catalog catalog, EnvironmentDescriptor environment)
{
    /// <param name="workload">The parsed workload definition, already schema-validated.</param>
    /// <param name="workloadFile">Name reported in errors about the workload itself.</param>
    public ExpansionResult Expand(JObject workload, string workloadFile)
    {
        var name = (string?)workload["metadata"]?["name"] ?? string.Empty;
        var team = (string?)workload["metadata"]?["team"] ?? string.Empty;
        var requirements = new List<ExpandedRequirement>();
        var errors = new List<LoadError>();
        var index = 0;
        foreach (var entry in workload["requires"] as JArray ?? [])
        {
            var type = (string?)entry["type"] ?? string.Empty;
            var requirement = new Requirement(
                (string?)entry["id"] ?? type,
                type,
                (string?)entry["class"] ?? Requirement.DefaultClass);
            var location = $"requires[{index++}]";
            if (ExpandOne(requirement, name, team, workloadFile, location, errors) is { } expanded)
            {
                requirements.Add(expanded);
            }
        }

        return new ExpansionResult(requirements, errors);
    }

    private ExpandedRequirement? ExpandOne(Requirement requirement, string workloadName, string workloadTeam, string workloadFile, string location, List<LoadError> errors)
    {
        var mapping = MappingMatcher.Select(catalog.Mappings, requirement, environment.Tier, workloadFile, location, errors);
        if (mapping is null)
        {
            return null;
        }

        var context = new ExpressionContext(
            catalog.Roles,
            catalog.Naming,
            environment,
            workloadName,
            workloadTeam,
            requirement.Id,
            mapping.Nodes.Keys.ToHashSet(),
            new Dictionary<Reference, string>());
        var evaluator = new ExpressionEvaluator(context);

        // Mapping problems are reported against the mapping file; the prefix says which requirement exposed them.
        var found = new List<LoadError>();
        var nodes = new List<ExpandedNode>();
        foreach (var (nodeName, node) in mapping.Nodes)
        {
            var config = WalkConfig(node.Config, evaluator, mapping.Source, $"nodes.{nodeName}.config", found);
            nodes.Add(new ExpandedNode(nodeName, node.Template, node.Kind, config.Properties));
        }

        var exports = new Dictionary<string, EvalResult>();
        foreach (var (key, text) in mapping.Exports)
        {
            if (evaluator.Evaluate(text, mapping.Source, $"exports.{key}", found) is { } result)
            {
                exports[key] = result;
            }
        }

        if (found.Count > 0)
        {
            errors.AddRange(found.Select(e => e with { Message = $"for {location} ('{requirement.Id}'): {e.Message}" }));
            return null;
        }

        return new ExpandedRequirement(requirement.Id, requirement.Type, requirement.Class, mapping.Source, nodes, exports);
    }

    /// <summary>Walks each top-level field of a node's config; a field that failed to evaluate is omitted (see <see cref="Walk"/>).</summary>
    internal static ConfigObject WalkConfig(IReadOnlyDictionary<string, JToken> config, ExpressionEvaluator evaluator, string file, string location, ICollection<LoadError> errors)
    {
        var properties = new Dictionary<string, ConfigValue>();
        foreach (var (key, value) in config)
        {
            if (Walk(value, evaluator, file, $"{location}.{key}", errors) is { } walked)
            {
                properties[key] = walked;
            }
        }

        return new ConfigObject(properties);
    }

    // Null means a string failed to evaluate. The failure is already in errors and the whole requirement is discarded,
    // so the surrounding object or array simply omits the value.
    internal static ConfigValue? Walk(JToken token, ExpressionEvaluator evaluator, string file, string location, ICollection<LoadError> errors)
    {
        switch (token)
        {
            case JObject obj:
                var properties = new Dictionary<string, ConfigValue>();
                foreach (var property in obj.Properties())
                {
                    if (Walk(property.Value, evaluator, file, $"{location}.{property.Name}", errors) is { } value)
                    {
                        properties[property.Name] = value;
                    }
                }

                return new ConfigObject(properties);
            case JArray array:
                return new ConfigArray(array
                    .Select((item, i) => Walk(item, evaluator, file, $"{location}[{i}]", errors))
                    .OfType<ConfigValue>()
                    .ToList());
            case JValue { Type: JTokenType.String } text:
                return evaluator.Evaluate((string?)text ?? string.Empty, file, location, errors) is { } result ? new ConfigText(result) : null;
            default:
                // JTokens are mutable; never hand out the catalog's own.
                return new ConfigScalar(token.DeepClone());
        }
    }
}
