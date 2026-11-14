using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expressions;
using jd.resolver.workload;

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

    private static readonly IReadOnlyDictionary<Reference, string> NoOutputs = new Dictionary<Reference, string>();
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NoParts = new Dictionary<string, IReadOnlyDictionary<string, string>>();

    // A part an environment run deploys, named after its owner, with the file the owner was read from.
    private sealed record RunPart(string Name, string Source, Part Part);

    // Where the environment's offers were read; errors about what they reach are reported there.
    private string OffersLocation => environment.Location(Roles.EnvironmentOffers);

    /// <param name="workload">The workload, loaded through <see cref="WorkloadFile"/>.</param>
    /// <param name="workloadFile">Name reported in errors about the workload itself.</param>
    public ExpansionResult Expand(Workload workload, string workloadFile)
    {
        var owner = new WorkloadOwner(workload.Name, workload.Team, workload.Image, workload.Port);
        var errors = new List<LoadError>();
        var requirements = new List<ExpandedRequirement>();
        foreach (var requirement in workload.Requirements)
        {
            if (ExpandOne(requirement, owner, workloadFile, errors) is { } expanded)
            {
                requirements.Add(expanded);
            }
        }

        var variables = new WorkloadVariables(workload.Variables, workloadFile, workload.VariablesLocation);
        var runtime = ExpandRuntime(owner, variables, requirements, workloadFile, errors);
        return new ExpansionResult
        {
            Kind = OwnerKind.Workload,
            OwnerFile = workloadFile,
            Owner = owner,
            Requirements = requirements,
            DeclaredTypes = workload.Requirements.Select(requirement => requirement.Type).ToHashSet(),
            Runtime = runtime,
            Errors = errors,
            Parts = NoParts,
            RequirementsLocation = workload.RequirementsLocation,
            VariablesLocation = workload.VariablesLocation,
        };
    }

    /// <summary>
    /// Collects what the offers of the environment, a definition resolved over <paramref name="baseDescriptor"/>, reach in its type's layer into the one scope <see cref="SubstrateScope"/>
    /// (docs/architecture/components.md section 3): the layer's shared resources reached through <c>uses</c> and, in the last layer (where
    /// workloads deploy), the offered components' parts of that layer. Every resource of an earlier layer it reaches must be in
    /// <paramref name="baseDescriptor"/>, which it reads but never deploys. The definition has no team and no runtime.
    /// </summary>
    public ExpansionResult ExpandEnvironment(string definitionFile, EnvironmentDescriptor? baseDescriptor)
    {
        var errors = new List<LoadError>();
        void Fail(string location, string message) => errors.Add(new LoadError(definitionFile, location, message));

        // The type is checked against the catalog before an environment is resolved (EnvironmentTypeCheck).
        var layer = catalog.EnvironmentTypes[environment.Type].Layer;
        var (parts, uses) = Offered(layer, Fail);
        parts.AddRange(SharedParts(layer, uses, baseDescriptor, Fail));
        CheckNew(parts, baseDescriptor, Fail);

        var owner = new WorkloadOwner(EnvironmentOwnerPrefix + environment.Name, string.Empty, null, null);
        var exports = parts.ToDictionary(p => p.Name, p => p.Part.Exports);
        var requirements = errors.Count == 0 ? EvaluateParts(owner, parts, exports, errors) : [];
        return new ExpansionResult
        {
            Kind = OwnerKind.Environment,
            OwnerFile = definitionFile,
            Owner = owner,
            Requirements = requirements,
            DeclaredTypes = new HashSet<string>(),
            Runtime = null,
            Errors = errors,
            Parts = exports,
        };
    }

    // The offered components' parts of the layer and the shared resources the offers use; an environment offers at most one runtime.
    private (List<RunPart> Parts, List<string> Uses) Offered(string layer, Action<string, string> fail)
    {
        var parts = new List<RunPart>();
        var uses = new List<string>();
        var runtimes = new List<string>();
        foreach (var (offer, index) in environment.Offers.Select((offer, index) => (offer, index)))
        {
            if (catalog.Components.TryGetValue(offer, out var component))
            {
                uses.AddRange(component.Reads);
                parts.AddRange(LayerParts(component, layer, $"{OffersLocation}[{index}]", fail));
            }
            else if (catalog.Runtimes.TryGetValue(offer, out var runtime))
            {
                uses.AddRange(runtime.Reads);
                runtimes.Add(offer);
            }
            else
            {
                fail($"{OffersLocation}[{index}]", $"'{offer}' is not a component or runtime in the catalog.");
            }
        }

        if (runtimes.Count > 1)
        {
            fail(OffersLocation, $"an environment offers at most one runtime; it offers {string.Join(", ", runtimes)}.");
        }

        return (parts, uses);
    }

    // An offered component's parts of the layer: only the last layer, where workloads deploy, deploys a component's environment parts.
    private List<RunPart> LayerParts(PartOwner component, string layer, string location, Action<string, string> fail)
    {
        if (layer != catalog.Layers[^1])
        {
            return [];
        }

        var (selected, miss) = component.Select(Selecting(), scope => scope == layer);
        if (miss is not null)
        {
            fail(location, miss.Message($"component '{component.Name}'"));
        }

        return [.. selected.Select(part => new RunPart(component.Name, component.Source, part))];
    }

    // The parts of the layer of the shared resources the offers reach. One owner per shared stack (security boundary 8): an environment layer
    // reads the shared resources of an earlier layer from its base, never deploys them.
    private List<RunPart> SharedParts(string layer, IEnumerable<string> uses, EnvironmentDescriptor? baseDescriptor, Action<string, string> fail)
    {
        var order = catalog.Layers.ToList();
        var missingInBase = baseDescriptor is null ? "there is no base descriptor" : $"base descriptor '{baseDescriptor.Name}' has no values for it";
        var parts = new List<RunPart>();
        foreach (var (resource, part) in Reached(uses).Select(r => catalog.SharedResources[r]).SelectMany(r => r.Parts.Select(part => (r, part))))
        {
            if (part.Scope == layer)
            {
                parts.Add(new RunPart(resource.Name, resource.Source, part));
            }
            else if (order.IndexOf(part.Scope) < order.IndexOf(layer) && !InBase(baseDescriptor, resource.Name))
            {
                fail(OffersLocation, $"the offers reach shared resource '{resource.Name}', but {missingInBase}; an environment never deploys a shared resource, so pass as the base the descriptor of a shared layer that offers it.");
            }
        }

        return parts;
    }

    // The descriptor adds new keys only, so a part of this run cannot be one the base already has.
    private void CheckNew(IEnumerable<RunPart> parts, EnvironmentDescriptor? baseDescriptor, Action<string, string> fail)
    {
        foreach (var part in parts.Where(p => InBase(baseDescriptor, p.Name)))
        {
            fail(OffersLocation, $"part '{part.Name}' is already in the base descriptor's values; an environment adds new parts only.");
        }
    }

    private static bool InBase(EnvironmentDescriptor? baseDescriptor, string part) =>
        baseDescriptor is not null && baseDescriptor.Values.Keys.Any(key => key.StartsWith(part + ".", StringComparison.Ordinal));

    // An environment run's parts share one scope: each may read every node of the run, and every part's exports in place.
    private List<ExpandedRequirement> EvaluateParts(
        WorkloadOwner owner, List<RunPart> parts, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> exports, List<LoadError> errors)
    {
        var evaluator = EvaluatorFor(owner, new ExpressionScope(SubstrateScope, parts.SelectMany(p => p.Part.Nodes.Keys).ToHashSet(), NoOutputs, exports));
        return [.. parts.Select(p => new ExpandedRequirement(
            SubstrateScope, null, null, p.Source, EvaluateNodes(p.Source, p.Part.Nodes, errors, (_, _) => (evaluator, null)), new Dictionary<string, EvalResult>()))];
    }

    // Everything reached through uses; the catalog loader keeps uses acyclic and naming shared resources only.
    private SortedSet<string> Reached(IEnumerable<string> uses)
    {
        var reached = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(uses);
        while (pending.TryDequeue(out var name))
        {
            if (reached.Add(name))
            {
                catalog.SharedResources[name].Reads.ToList().ForEach(pending.Enqueue);
            }
        }

        return reached;
    }

    private ExpressionEvaluator EvaluatorFor(WorkloadOwner owner, ExpressionScope scope) => new(new ExpressionContext(catalog.Roles, catalog.Naming, environment, owner, scope));

    // Exact lookup, no matching: the component named by the type, which the environment must offer (security boundary 11), and its part
    // the environment's type and the class select.
    private ExpandedRequirement? ExpandOne(Requirement requirement, WorkloadOwner owner, string ownerFile, List<LoadError> errors)
    {
        void Fail(string message) => errors.Add(new LoadError(ownerFile, requirement.Location, $"requirement '{requirement.Id}': {message}"));
        if (!catalog.Components.TryGetValue(requirement.Type, out var component))
        {
            Fail($"'{requirement.Type}' is not a component in the catalog.");
            return null;
        }

        if (!environment.Offers.Contains(component.Name))
        {
            Fail($"environment '{environment.Name}' does not offer {component.Name} (offers: {string.Join(", ", environment.Offers)}).");
            return null;
        }

        var keys = Selecting();
        keys[Roles.RequirementClass] = requirement.Class;
        var (selected, miss) = component.Select(keys, IsWorkloadScope);
        if (miss is not null)
        {
            Fail(miss.Message($"component '{component.Name}'"));
            return null;
        }

        // Both maps selected, so one class part is left: the schema gives every implementation at least one class.
        var part = selected.Single();

        // Component problems are reported against the component file; the prefix says which requirement exposed them.
        var found = new List<LoadError>();
        var evaluator = EvaluatorFor(owner, new ExpressionScope(requirement.Id, part.Nodes.Keys.ToHashSet(), NoOutputs, NoParts));
        var nodes = EvaluateNodes(component.Source, part.Nodes, found, (_, _) => (evaluator, null));
        var exports = EvaluateExports(component.Source, part, evaluator, found);
        if (found.Count > 0)
        {
            errors.AddRange(found.Select(e => e with { Message = $"for {requirement.Location} ('{requirement.Id}'): {e.Message}" }));
            return null;
        }

        return new ExpandedRequirement(requirement.Id, requirement.Type, requirement.Class, component.Source, nodes, exports) { ExportLocations = part.ExportLocations };
    }

    // What selects a part here: the environment's type; a requirement adds its class.
    private Dictionary<string, string> Selecting() => new() { [Roles.EnvironmentType] = environment.Type };

    // Workloads deploy in a scope that is no layer.
    private bool IsWorkloadScope(string scope) => !catalog.Layers.Contains(scope);

    // Every workload gets the runtime the environment offers, in the implementation for the environment's type; each node is evaluated
    // with its own name as the current id. A workload variable reading an export that is already a value resolves here; the others wait
    // for the second pass.
    private ExpandedRuntime? ExpandRuntime(
        WorkloadOwner owner, WorkloadVariables variables, List<ExpandedRequirement> requirements, string ownerFile, List<LoadError> errors)
    {
        const string location = ExpressionEvaluator.RuntimeNode;
        var offered = environment.Offers.Where(catalog.Runtimes.ContainsKey).ToList();
        if (offered is not [var name])
        {
            // env up lets a definition offer at most one runtime; a descriptor is still read as written.
            errors.Add(new LoadError(ownerFile, location, offered.Count == 0
                ? $"environment '{environment.Name}' offers no runtime, so no workload can deploy on it."
                : $"environment '{environment.Name}' offers more than one runtime ({string.Join(", ", offered)}); a workload runs on exactly one."));
            return null;
        }

        var runtime = catalog.Runtimes[name];
        var (selected, miss) = runtime.Select(Selecting(), IsWorkloadScope);
        if (miss is not null)
        {
            errors.Add(new LoadError(ownerFile, location, miss.Message($"runtime '{runtime.Name}'")));
            return null;
        }

        // The runtime's implementation map selected, so its one part is left.
        var part = selected.Single();
        var (probe, release) = catalog.Rollouts[Catalog.RolloutKey(runtime, part)];

        var known = new Dictionary<Reference, string>();
        foreach (var requirement in requirements)
        {
            foreach (var (export, result) in requirement.Exports.Where(e => e.Value is Resolved))
            {
                known[new Reference(ReferenceKind.Resource, requirement.Id, export)] = ((Resolved)result).Value;
            }
        }

        var nodeNames = part.Nodes.Keys.ToHashSet();
        var found = new List<LoadError>();
        var nodes = EvaluateNodes(runtime.Source, part.Nodes, found, (name, kind) => IsRuntimeNode(name, kind)
            ? (EvaluatorFor(owner, new ExpressionScope(name, nodeNames, known, NoParts)), variables)
            : (EvaluatorFor(owner, new ExpressionScope(name, nodeNames, NoOutputs, NoParts)), null));
        if (found.Count > 0)
        {
            errors.AddRange(found.Select(e => e with { Message = $"for the runtime: {e.Message}" }));
            return null;
        }

        return new ExpandedRuntime(runtime.Source, nodes, probe, release);
    }

    // Only the runtime node (never a grant) reads the workload's variables and the requirements' exports; any other node that tries is
    // an error, reported where it is found.
    private static bool IsRuntimeNode(string name, NodeKind kind) => name == ExpressionEvaluator.RuntimeNode && kind != NodeKind.Grant;

    // Source says where the nodes are written, for errors: the catalog file; each node knows its location in it.
    private static List<ExpandedNode> EvaluateNodes(
        string source, IReadOnlyDictionary<string, Node> partNodes, List<LoadError> found,
        Func<string, NodeKind, (ExpressionEvaluator Evaluator, WorkloadVariables? Variables)> contextFor)
    {
        var nodes = new List<ExpandedNode>();
        foreach (var (nodeName, node) in partNodes)
        {
            var (evaluator, variables) = contextFor(nodeName, node.Kind);
            nodes.Add(new ExpandedNode(nodeName, node.Template, node.Kind,
                ConfigWalker.WalkConfig(node.Config, evaluator, source, node.ConfigLocation, found, variables).Properties));
        }

        return nodes;
    }

    private static Dictionary<string, EvalResult> EvaluateExports(string source, Part part, ExpressionEvaluator evaluator, List<LoadError> found)
    {
        var exports = new Dictionary<string, EvalResult>();
        foreach (var (key, text) in part.Exports)
        {
            if (evaluator.Evaluate(text, source, part.ExportLocations[key], found) is { } result)
            {
                exports[key] = result;
            }
        }

        return exports;
    }
}
