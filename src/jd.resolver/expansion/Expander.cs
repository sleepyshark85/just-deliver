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
    /// <summary>Owner-name prefix of environment definitions. A workload name cannot start with it, so substrate and workload ids, stacks and <c>name()</c> hashes never collide.</summary>
    public const string EnvironmentOwnerPrefix = "@";

    private sealed record Owner(OwnerKind Kind, string Name, string Team, string? Image, int? Port, JObject Variables);

    /// <param name="workload">The parsed workload definition, already schema-validated.</param>
    /// <param name="workloadFile">Name reported in errors about the workload itself.</param>
    public ExpansionResult Expand(JObject workload, string workloadFile)
    {
        var container = workload["container"];
        var owner = new Owner(
            OwnerKind.Workload,
            (string?)workload["metadata"]?["name"] ?? string.Empty,
            (string?)workload["metadata"]?["team"] ?? string.Empty,
            (string?)container?["image"],
            (int?)(container?["ports"] as JArray)?.FirstOrDefault()?["port"],
            container?["variables"] as JObject ?? new JObject());
        return Expand(owner, workload["requires"] as JArray ?? [], workloadFile);
    }

    /// <summary>Expands the substrate <paramref name="requires"/> (entries with <c>type</c>, optional <c>id</c> and <c>class</c>) of the environment definition <paramref name="name"/>; it has no team and no runtime.</summary>
    public ExpansionResult ExpandEnvironment(string name, JArray requires, string ownerFile) =>
        Expand(new Owner(OwnerKind.Environment, EnvironmentOwnerPrefix + name, string.Empty, null, null, new JObject()), requires, ownerFile);

    private ExpansionResult Expand(Owner owner, JArray requires, string ownerFile)
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
            if (ExpandOne(requirement, owner, ownerFile, location, errors) is { } expanded)
            {
                requirements.Add(expanded);
            }
        }

        var runtime = owner.Kind == OwnerKind.Workload ? ExpandRuntime(owner, requirements, ownerFile, errors) : null;
        return new ExpansionResult(owner.Kind, owner.Name, owner.Team, owner.Image, owner.Port, requirements, runtime, errors);
    }

    private ExpressionEvaluator EvaluatorFor(Owner owner, string currentId, IEnumerable<string> nodeNames, IReadOnlyDictionary<Reference, string>? known = null) => new(new ExpressionContext(
        catalog.Roles, catalog.Naming, environment, owner.Name, owner.Team, currentId, nodeNames.ToHashSet(), known ?? new Dictionary<Reference, string>(), owner.Image, owner.Port));

    private ExpandedRequirement? ExpandOne(Requirement requirement, Owner owner, string ownerFile, string location, List<LoadError> errors)
    {
        var mapping = MappingMatcher.Select(catalog.Mappings, requirement, environment.Tier, ownerFile, location, errors);
        if (mapping is null)
        {
            return null;
        }

        // Mapping problems are reported against the mapping file; the prefix says which requirement exposed them.
        var found = new List<LoadError>();
        var evaluator = EvaluatorFor(owner, requirement.Id, mapping.Nodes.Keys);
        var nodes = EvaluateNodes(mapping, _ => evaluator, null, string.Empty, found);
        var exports = EvaluateExports(mapping, evaluator, found);
        if (found.Count > 0)
        {
            errors.AddRange(found.Select(e => e with { Message = $"for {location} ('{requirement.Id}'): {e.Message}" }));
            return null;
        }

        return new ExpandedRequirement(requirement.Id, requirement.Type, requirement.Class, mapping.Source, nodes, exports);
    }

    // The runtime is a mapping like any other, selected by its kind; each node is evaluated with its own name as the current id.
    // A workload variable reading an export that is already a value resolves here; the others wait for the second pass.
    private ExpandedRuntime? ExpandRuntime(Owner owner, List<ExpandedRequirement> requirements, string ownerFile, List<LoadError> errors)
    {
        var mapping = MappingMatcher.SelectRuntime(catalog.Mappings, environment.Tier, ownerFile, errors);
        if (mapping is null)
        {
            return null;
        }

        var known = new Dictionary<Reference, string>();
        foreach (var requirement in requirements)
        {
            foreach (var (export, result) in requirement.Exports.Where(e => e.Value is Resolved))
            {
                known[new Reference(ReferenceKind.Resource, requirement.Id, export)] = ((Resolved)result).Value;
            }
        }

        var found = new List<LoadError>();
        var nodes = EvaluateNodes(mapping, name => EvaluatorFor(owner, name, mapping.Nodes.Keys, known), owner.Variables, ownerFile, found);
        if (found.Count > 0)
        {
            errors.AddRange(found.Select(e => e with { Message = $"for the runtime: {e.Message}" }));
            return null;
        }

        return new ExpandedRuntime(mapping.Source, nodes, mapping.Probe, ownerFile);
    }

    // variables are the workload's, set only for the runtime mapping: they are the source of its fn::entries forms.
    private static List<ExpandedNode> EvaluateNodes(
        Mapping mapping, Func<string, ExpressionEvaluator> evaluatorFor, JObject? variables, string workloadFile, List<LoadError> found)
    {
        var nodes = new List<ExpandedNode>();
        foreach (var (nodeName, node) in mapping.Nodes)
        {
            var config = node.Config.ToDictionary(c => c.Key, c => variables is null ? c.Value : WithEntriesSource(c.Value, variables));
            var problems = new List<LoadError>();
            nodes.Add(new ExpandedNode(nodeName, node.Template, node.Kind,
                ConfigWalker.WalkConfig(config, evaluatorFor(nodeName), mapping.Source, $"nodes.{nodeName}.config", problems).Properties));
            found.AddRange(problems.Select(e => InWorkload(e, workloadFile)));
        }

        return nodes;
    }

    // A problem inside the substituted variables is the workload's: it is reported at container.variables.<NAME> of its file.
    private static LoadError InWorkload(LoadError error, string workloadFile)
    {
        var marker = ConfigWalker.EntriesKey + ".";
        var at = error.Location.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 || workloadFile.Length == 0 ? error : error with { File = workloadFile, Location = "container.variables." + error.Location[(at + marker.Length)..] };
    }

    // The source of an fn::entries form is the workload's variables; the walker then evaluates them like any other map.
    private static JToken WithEntriesSource(JToken token, JObject variables) => token switch
    {
        JObject obj => new JObject(obj.Properties().Select(p => new JProperty(
            p.Name,
            p is { Name: ConfigWalker.EntriesKey, Value: JValue { Type: JTokenType.String } source } && (string?)source == ConfigWalker.EntriesSource ? variables.DeepClone() : WithEntriesSource(p.Value, variables)))),
        JArray array => new JArray(array.Select(item => WithEntriesSource(item, variables))),
        _ => token,
    };

    private static Dictionary<string, EvalResult> EvaluateExports(Mapping mapping, ExpressionEvaluator evaluator, List<LoadError> found)
    {
        var exports = new Dictionary<string, EvalResult>();
        foreach (var (key, text) in mapping.Exports)
        {
            if (evaluator.Evaluate(text, mapping.Source, $"exports.{key}", found) is { } result)
            {
                exports[key] = result;
            }
        }

        return exports;
    }
}
