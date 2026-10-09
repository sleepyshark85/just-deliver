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
    public ExpansionResult Expand(JObject workload, string workloadFile) =>
        Expand((string?)workload["metadata"]?["name"] ?? string.Empty, (string?)workload["metadata"]?["team"] ?? string.Empty, workload["requires"] as JArray ?? [], workloadFile);

    /// <summary>Expands <paramref name="requires"/> (entries with <c>type</c>, optional <c>id</c> and <c>class</c>) for an owner that is not a workload document.</summary>
    public ExpansionResult Expand(string name, string team, JArray requires, string workloadFile)
    {
        var requirements = new List<ExpandedRequirement>();
        var errors = new List<LoadError>();
        var index = 0;
        foreach (var entry in requires)
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

        return new ExpansionResult(name, team, requirements, errors);
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
            var config = ConfigWalker.WalkConfig(node.Config, evaluator, mapping.Source, $"nodes.{nodeName}.config", found);
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
}
