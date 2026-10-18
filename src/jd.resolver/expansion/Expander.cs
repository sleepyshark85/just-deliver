using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.expansion;

/// <summary>
/// Turns each requirement of a validated workload into the nodes of its component's class, in the implementation for the environment's
/// type, with config and exports evaluated. Pure; the catalog is never modified. Rules: docs/architecture/resolver.md.
/// </summary>
public sealed class Expander(Catalog catalog, EnvironmentDescriptor environment)
{
    /// <summary>Owner-name prefix of environment definitions. A workload name cannot start with it, so substrate and workload ids, stacks and <c>name()</c> hashes never collide.</summary>
    public const string EnvironmentOwnerPrefix = "@";

    private sealed record Owner(OwnerKind Kind, string Name, string Team, string? Image, int? Port, JObject Variables);

    /// <summary>A requirement with the format defaults applied: <c>id</c> falls back to the type, <c>class</c> to <see cref="DefaultClass"/>.</summary>
    private sealed record Requirement(string Id, string Type, string Class)
    {
        public const string DefaultClass = "standard";
    }

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
        var declaredTypes = new HashSet<string>();
        var index = 0;
        foreach (var entry in requires)
        {
            var type = (string?)entry["type"] ?? string.Empty;
            declaredTypes.Add(type);
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
        return new ExpansionResult(owner.Kind, ownerFile, owner.Name, owner.Team, owner.Image, owner.Port, requirements, declaredTypes, runtime, errors);
    }

    private ExpressionEvaluator EvaluatorFor(Owner owner, string currentId, IEnumerable<string> nodeNames, IReadOnlyDictionary<Reference, string>? known = null) => new(new ExpressionContext(
        catalog.Roles, catalog.Naming, environment, owner.Name, owner.Team, currentId, nodeNames.ToHashSet(), known ?? new Dictionary<Reference, string>(), owner.Image, owner.Port));

    // Exact lookup, no matching: the component named by the type, its implementation for the environment's type (else the default), the class.
    private ExpandedRequirement? ExpandOne(Requirement requirement, Owner owner, string ownerFile, string location, List<LoadError> errors)
    {
        void Fail(string message) => errors.Add(new LoadError(ownerFile, location, $"requirement '{requirement.Id}': {message}"));
        if (!catalog.Components.TryGetValue(requirement.Type, out var component))
        {
            Fail($"'{requirement.Type}' is not a component in the catalog.");
            return null;
        }

        if (ImplementationKey.For(component.Implementations, environment.Type) is not { } key)
        {
            Fail(NoImplementation($"component '{component.Name}'"));
            return null;
        }

        var classes = component.Implementations[key].Classes;
        if (!classes.TryGetValue(requirement.Class, out var part))
        {
            Fail($"component '{component.Name}' has no class '{requirement.Class}' for environment type '{environment.Type}' (classes: {string.Join(", ", classes.Keys)}).");
            return null;
        }

        // Component problems are reported against the component file; the prefix says which requirement exposed them.
        var found = new List<LoadError>();
        var path = $"implementations.{key}.classes.{requirement.Class}";
        var evaluator = EvaluatorFor(owner, requirement.Id, part.Nodes.Keys);
        var nodes = EvaluateNodes(component.Source, path, part.Nodes, found, (_, _) => (evaluator, null));
        var exports = EvaluateExports(component.Source, path, part.Exports, evaluator, found);
        if (found.Count > 0)
        {
            errors.AddRange(found.Select(e => e with { Message = $"for {location} ('{requirement.Id}'): {e.Message}" }));
            return null;
        }

        return new ExpandedRequirement(requirement.Id, requirement.Type, requirement.Class, component.Source, nodes, exports);
    }

    private string NoImplementation(string owner) =>
        $"{owner} has no implementation for environment type '{environment.Type}' and no '{ImplementationKey.Default}' implementation.";

    // Every workload gets the catalog's one runtime, in the implementation for the environment's type; each node is evaluated with its own
    // name as the current id. A workload variable reading an export that is already a value resolves here; the others wait for the second pass.
    private ExpandedRuntime? ExpandRuntime(Owner owner, List<ExpandedRequirement> requirements, string ownerFile, List<LoadError> errors)
    {
        const string location = ExpressionEvaluator.RuntimeNode;
        if (catalog.Runtimes.Values.ToList() is not [var runtime])
        {
            errors.Add(new LoadError(ownerFile, location, $"the catalog must declare exactly one Runtime; it declares {catalog.Runtimes.Count}."));
            return null;
        }

        if (ImplementationKey.For(runtime.Implementations, environment.Type) is not { } key)
        {
            errors.Add(new LoadError(ownerFile, location, NoImplementation($"runtime '{runtime.Name}'")));
            return null;
        }

        var implementation = runtime.Implementations[key];

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
        var nodes = EvaluateNodes(runtime.Source, $"implementations.{key}", implementation.Nodes, found, (name, kind) => IsRuntimeNode(name, kind)
            ? (EvaluatorFor(owner, name, implementation.Nodes.Keys, known), variables)
            : (EvaluatorFor(owner, name, implementation.Nodes.Keys), null));
        if (found.Count > 0)
        {
            errors.AddRange(found.Select(e => e with { Message = $"for the runtime: {e.Message}" }));
            return null;
        }

        return new ExpandedRuntime(runtime.Source, nodes, implementation.Probe, implementation.Release);
    }

    // Only the runtime node (never a grant) reads the workload's variables and the requirements' exports; any other node that tries is
    // an error, reported where it is found.
    private static bool IsRuntimeNode(string name, NodeKind kind) => name == ExpressionEvaluator.RuntimeNode && kind != NodeKind.Grant;

    // Source and path say where the nodes are written, for errors: the catalog file and the part's location in it.
    private static List<ExpandedNode> EvaluateNodes(
        string source, string path, IReadOnlyDictionary<string, Node> partNodes, List<LoadError> found,
        Func<string, NodeKind, (ExpressionEvaluator Evaluator, WorkloadVariables? Variables)> contextFor)
    {
        var nodes = new List<ExpandedNode>();
        foreach (var (nodeName, node) in partNodes)
        {
            var (evaluator, variables) = contextFor(nodeName, node.Kind);
            nodes.Add(new ExpandedNode(nodeName, node.Template, node.Kind,
                ConfigWalker.WalkConfig(node.Config, evaluator, source, $"{path}.nodes.{nodeName}.config", found, variables).Properties));
        }

        return nodes;
    }

    private static Dictionary<string, EvalResult> EvaluateExports(
        string source, string path, IReadOnlyDictionary<string, string> partExports, ExpressionEvaluator evaluator, List<LoadError> found)
    {
        var exports = new Dictionary<string, EvalResult>();
        foreach (var (key, text) in partExports)
        {
            if (evaluator.Evaluate(text, source, $"{path}.exports.{key}", found) is { } result)
            {
                exports[key] = result;
            }
        }

        return exports;
    }
}
