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
    /// <summary>The one scope an environment run's parts expand into, and so the current id of their <c>name()</c> hashes. An engine constant.</summary>
    public const string SubstrateScope = "substrate";

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

    /// <summary>
    /// Collects what an environment definition's offers reach in its type's layer into the one scope <see cref="SubstrateScope"/>
    /// (docs/architecture/components.md section 3): the layer's shared resources reached through <c>uses</c> and, in an environment layer,
    /// the offered components' environment parts. Every shared-scope resource an environment layer reaches must be in
    /// <paramref name="baseDescriptor"/>, which it reads but never deploys. The definition has no team and no runtime.
    /// </summary>
    public ExpansionResult ExpandEnvironment(EnvironmentDefinition definition, string definitionFile, EnvironmentDescriptor? baseDescriptor)
    {
        var owner = new Owner(OwnerKind.Environment, EnvironmentOwnerPrefix + definition.Name, string.Empty, null, null, new JObject());
        var errors = new List<LoadError>();
        void Fail(string location, string message) => errors.Add(new LoadError(definitionFile, location, message));

        // The type is checked against the catalog before an environment is resolved (EnvironmentTypeCheck).
        var layer = catalog.EnvironmentTypes[environment.Type].Layer;
        var parts = new List<(string Name, string Source, string Path, Part Part)>();
        var uses = new List<string>();
        var runtimes = new List<string>();
        foreach (var (offer, index) in definition.Offers.Select((o, i) => (o, i)))
        {
            if (catalog.Components.TryGetValue(offer, out var component))
            {
                uses.AddRange(component.Uses);
                if (layer == SharedScope.Shared)
                {
                    continue;
                }

                if (ImplementationKey.For(component.Implementations, environment.Type) is not { } key)
                {
                    Fail($"offers[{index}]", NoImplementation($"component '{offer}'"));
                }
                else if (component.Implementations[key].Environment is { } part)
                {
                    parts.Add((offer, component.Source, $"implementations.{key}.environment.", part));
                }
            }
            else if (catalog.Runtimes.TryGetValue(offer, out var runtime))
            {
                uses.AddRange(runtime.Uses);
                runtimes.Add(offer);
            }
            else
            {
                Fail($"offers[{index}]", $"'{offer}' is not a component or runtime in the catalog.");
            }
        }

        if (runtimes.Count > 1)
        {
            Fail("offers", $"an environment offers at most one runtime; it offers {string.Join(", ", runtimes)}.");
        }

        // Everything the offers reach through uses; the catalog loader keeps uses acyclic and naming shared resources only.
        var reached = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(uses);
        while (pending.TryDequeue(out var name))
        {
            if (reached.Add(name))
            {
                catalog.SharedResources[name].Uses.ToList().ForEach(pending.Enqueue);
            }
        }

        // One owner per shared stack (security boundary 8): an environment layer reads the shared resources from its base, never deploys them.
        var baseValues = baseDescriptor?.Values ?? new Dictionary<string, string>();
        var baseName = baseDescriptor is null ? "there is no base descriptor" : $"base descriptor '{baseDescriptor.Name}' has no values for it";
        foreach (var resource in reached.Select(r => catalog.SharedResources[r]))
        {
            if (resource.Scope == layer)
            {
                parts.Add((resource.Name, resource.Source, string.Empty, resource.Part));
            }
            else if (layer == SharedScope.Environment && !resource.Part.Exports.Keys.All(e => baseValues.ContainsKey($"{resource.Name}.{e}")))
            {
                Fail("offers", $"the offers reach shared resource '{resource.Name}', but {baseName}; an environment never deploys a shared resource, so pass as the base the descriptor of a shared layer that offers it.");
            }
        }

        // The descriptor adds new keys only, so a part of this run cannot be one the base already has.
        foreach (var part in parts.Where(p => baseValues.Keys.Any(k => k.StartsWith(p.Name + ".", StringComparison.Ordinal))))
        {
            Fail("offers", $"part '{part.Name}' is already in the base descriptor's values; an environment adds new parts only.");
        }

        var exports = parts.ToDictionary(p => p.Name, p => p.Part.Exports);
        var requirements = new List<ExpandedRequirement>();
        if (errors.Count == 0)
        {
            var evaluator = EvaluatorFor(owner, SubstrateScope, parts.SelectMany(p => p.Part.Nodes.Keys), parts: exports);
            requirements.AddRange(parts.Select(p => new ExpandedRequirement(
                SubstrateScope, null, null, p.Source, EvaluateNodes(p.Source, p.Path, p.Part.Nodes, errors, (_, _) => (evaluator, null)), new Dictionary<string, EvalResult>())));
        }

        return new ExpansionResult(owner.Kind, definitionFile, owner.Name, owner.Team, null, null, requirements, new HashSet<string>(), null, errors, exports);
    }

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

        var runtime = ExpandRuntime(owner, requirements, ownerFile, errors);
        return new ExpansionResult(owner.Kind, ownerFile, owner.Name, owner.Team, owner.Image, owner.Port, requirements, declaredTypes, runtime, errors, new Dictionary<string, IReadOnlyDictionary<string, string>>());
    }

    private ExpressionEvaluator EvaluatorFor(
        Owner owner, string currentId, IEnumerable<string> nodeNames, IReadOnlyDictionary<Reference, string>? known = null, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? parts = null) =>
        new(new ExpressionContext(
            catalog.Roles, catalog.Naming, environment, owner.Name, owner.Team, currentId, nodeNames.ToHashSet(), known ?? new Dictionary<Reference, string>(), owner.Image, owner.Port,
            parts ?? new Dictionary<string, IReadOnlyDictionary<string, string>>()));

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
        var nodes = EvaluateNodes(component.Source, $"{path}.", part.Nodes, found, (_, _) => (evaluator, null));
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
        var nodes = EvaluateNodes(runtime.Source, $"implementations.{key}.", implementation.Nodes, found, (name, kind) => IsRuntimeNode(name, kind)
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

    // Source and prefix say where the nodes are written, for errors: the catalog file and the part's location in it.
    private static List<ExpandedNode> EvaluateNodes(
        string source, string prefix, IReadOnlyDictionary<string, Node> partNodes, List<LoadError> found,
        Func<string, NodeKind, (ExpressionEvaluator Evaluator, WorkloadVariables? Variables)> contextFor)
    {
        var nodes = new List<ExpandedNode>();
        foreach (var (nodeName, node) in partNodes)
        {
            var (evaluator, variables) = contextFor(nodeName, node.Kind);
            nodes.Add(new ExpandedNode(nodeName, node.Template, node.Kind,
                ConfigWalker.WalkConfig(node.Config, evaluator, source, $"{prefix}nodes.{nodeName}.config", found, variables).Properties));
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
