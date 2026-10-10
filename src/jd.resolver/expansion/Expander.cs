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
        var nodes = EvaluateNodes(mapping, found, (_, _) => (evaluator, null));
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
        var variables = new WorkloadVariables(owner.Variables, ownerFile);
        var nodes = EvaluateNodes(mapping, found, (name, kind) => IsRuntimeNode(name, kind)
            ? (EvaluatorFor(owner, name, mapping.Nodes.Keys, known), variables)
            : (EvaluatorFor(owner, name, mapping.Nodes.Keys), null));
        if (found.Count > 0)
        {
            errors.AddRange(found.Select(e => e with { Message = $"for the runtime: {e.Message}" }));
            return null;
        }

        return new ExpandedRuntime(mapping.Source, nodes, mapping.Probe, mapping.Release, ownerFile);
    }

    // Only the runtime node (never a grant) reads the workload's variables and the requirements' exports; any other node that tries is
    // an error, reported where it is found.
    private static bool IsRuntimeNode(string name, NodeKind kind) => name == ExpressionEvaluator.RuntimeNode && kind != NodeKind.Grant;

    private static List<ExpandedNode> EvaluateNodes(
        Mapping mapping, List<LoadError> found, Func<string, NodeKind, (ExpressionEvaluator Evaluator, WorkloadVariables? Variables)> contextFor)
    {
        var nodes = new List<ExpandedNode>();
        foreach (var (nodeName, node) in mapping.Nodes)
        {
            var (evaluator, variables) = contextFor(nodeName, node.Kind);
            nodes.Add(new ExpandedNode(nodeName, node.Template, node.Kind,
                ConfigWalker.WalkConfig(node.Config, evaluator, mapping.Source, $"nodes.{nodeName}.config", found, variables).Properties));
        }

        return nodes;
    }

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
