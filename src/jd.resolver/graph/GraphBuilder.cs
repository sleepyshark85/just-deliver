using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.policies;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace jd.resolver.graph;

/// <summary>
/// Turns a <see cref="PolicyResult"/> into the <see cref="ResolvedGraph"/>: node ids and stacks, edges from pending
/// references, topological order, phases, hashes and the grant target check. Pure and deterministic.
/// Rules: docs/architecture/resolver.md (Output: ResolvedGraph). <paramref name="grantTargets"/>: the catalog's grant template to target input.
/// </summary>
public sealed class GraphBuilder(EnvironmentDescriptor environment, IReadOnlyDictionary<string, string> grantTargets)
{
    /// <summary>Scope of the runtime's nodes; requirement ids cannot contain '@'.</summary>
    public const string WorkloadScope = "@workload";

    private sealed record Draft(string Id, string Scope, string Stack, ResolvedNode Node, List<string> DependsOn);

    // What every node's analysis shares: the node ids by scope and name, the typed requirements' exports, the workload's runtime node (null
    // for an environment definition, which has none) and where the errors go.
    private sealed record BuildContext(
        Dictionary<(string Scope, string Name), string> Ids, Dictionary<string, IReadOnlyDictionary<string, EvalResult>> Exports, string? RuntimeId, List<LoadError> Errors);

    /// <summary>The graph of <paramref name="policy"/>'s nodes; its errors are the policy result's and those the build finds (references, cycles, grant targets).</summary>
    public ResolvedGraph Build(PolicyResult policy)
    {
        var errors = new List<LoadError>(policy.Errors);

        // An export is evaluated in its requirement's scope, where another requirement's exports are out of reach. The catalog loader
        // rejects it at the export; a graph built from anything else is still refused here, at the same location.
        foreach (var requirement in policy.Requirements)
        {
            foreach (var (name, result) in requirement.Exports.Where(e => e.Value is Pending { References: var refs } && refs.Any(r => r.Kind == ReferenceKind.Resource)))
            {
                errors.Add(new LoadError(requirement.Source, requirement.ExportLocations[name], $"requirement '{requirement.Id}': an export cannot reference ${{resource.<id>.<export>}}."));
            }
        }

        // An environment run's parts have no type and no contract; what they export is the graph's Parts.
        var typed = policy.Requirements.Where(r => r.Type is not null).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var exports = typed.ToDictionary(r => r.Id, r => r.Exports);
        var entries = policy.Requirements.SelectMany(r => r.Nodes.Select(n => (Scope: r.Id, Node: n)))
            .Concat(policy.RuntimeNodes.Select(n => (Scope: WorkloadScope, Node: n)))
            .Select(e => (e.Scope, e.Node, Id: $"{policy.WorkloadName}/{environment.Name}/{e.Scope}/{e.Node.Name}"))
            .ToList();

        var runtimeId = entries.Where(e => e.Scope == WorkloadScope && e.Node.Name == ExpressionEvaluator.RuntimeNode).Select(e => e.Id).FirstOrDefault();
        var context = new BuildContext(entries.ToDictionary(e => (e.Scope, e.Node.Name), e => e.Id), exports, runtimeId, errors);
        var drafts = entries
            .Select(e => Analyse(e.Scope, e.Node, e.Id, context))
            .ToDictionary(d => d.Id);
        var phases = new Dictionary<string, Phase>();
        var nodes = new List<GraphNode>();
        foreach (var draft in OrderNodes(drafts, errors).Select(id => drafts[id]))
        {
            phases[draft.Id] = draft.Id == runtimeId ? Phase.Runtime : draft.DependsOn.Any(d => phases[d] != Phase.Infrastructure) ? Phase.AfterRuntime : Phase.Infrastructure;
            nodes.Add(new GraphNode(
                draft.Id, draft.Scope, draft.Node.Name, draft.Node.Template, draft.Node.Kind, draft.Stack, phases[draft.Id],
                draft.Node.Config, draft.Node.Provenance, Hash(draft.Node), draft.DependsOn, draft.Node.Probe, draft.Node.Release));
        }

        return new ResolvedGraph(policy.CatalogVersion, environment.Name, policy.WorkloadName, policy.WorkloadTeam, policy.WorkloadImage, policy.WorkloadPort, nodes, exports, errors, policy.Parts)
        {
            ExportLocations = typed.ToDictionary(r => r.Id, r => r.ExportLocations),
        };
    }

    // Pulumi stack names allow [A-Za-z0-9_.-]. Ids contain no '.' or '_' (names are kebab; the owner and the scope may start
    // with '@'), so this is one-to-one and stacks are unique by construction.
    private static string Stack(string id) => id.Replace('/', '.').Replace('@', '_');

    // Finds a node's dependencies from its pending references and, for a grant, checks its target.
    private Draft Analyse(string scope, ResolvedNode node, string id, BuildContext context)
    {
        var dependsOn = new SortedSet<string>(StringComparer.Ordinal);
        if (node.Kind == NodeKind.Grant)
        {
            CheckGrantTarget(node, id, context.Errors);
        }

        foreach (var (field, text) in Texts(new ConfigObject(node.Config), string.Empty))
        {
            // A workload variable is reported against the workload's definition, where its author wrote it.
            var origin = node.Provenance.GetValueOrDefault(field);
            // A workload entry's provenance key is <field>.<NAME> (the config walk makes it so), hence the dot.
            var location = origin?.Layer == Layer.Workload && field.IndexOf('.') is var dot and > 0 ? origin.Rule + field[dot..] : field;
            void Fail(string message) => context.Errors.Add(new LoadError(origin?.Source ?? id, location, $"node '{id}': {message}"));

            foreach (var reference in (text.Result as Pending)?.References ?? (IEnumerable<Reference>)[])
            {
                // Only the runtime node reads requirements' exports (workload variables). Elsewhere one requirement could read another, or a
                // grant reach past its target check through an export (D19).
                if (reference.Kind == ReferenceKind.Resource && !(scope == WorkloadScope && node.Name == ExpressionEvaluator.RuntimeNode && node.Kind != NodeKind.Grant))
                {
                    Fail($"field '{field}' references resource.{reference.Target}.{reference.Output}; only the runtime node may read a requirement's export.");
                }
                else if (reference.Kind == ReferenceKind.Resource)
                {
                    FollowExport(reference, field, context.Exports, Fail, inner => AddDependency(inner, reference.Target, field, Fail));
                }
                else
                {
                    AddDependency(reference, scope, field, Fail);
                }
            }
        }

        // A reference to a node output, in the scope its expression was written in, is an edge to that node.
        void AddDependency(Reference reference, string inScope, string field, Action<string> fail)
        {
            if (reference.Target == ExpressionEvaluator.RuntimeNode)
            {
                if (context.RuntimeId is null)
                {
                    fail($"field '{field}' references runtime.{reference.Output}, but there is no runtime.");
                }
                else
                {
                    dependsOn.Add(context.RuntimeId);
                }
            }
            else if (context.Ids.TryGetValue((inScope, reference.Target), out var target))
            {
                dependsOn.Add(target);
            }
            else
            {
                // The evaluator rejects names outside the scope it was given.
                throw new UnreachableException($"node '{id}' references '{reference.Target}', which the evaluator should have rejected.");
            }
        }

        return new Draft(id, scope, Stack(id), node, dependsOn.ToList());
    }

    // A reference to a requirement's export: the export waits on the nodes of its requirement, so the node does too (dependOn, given the
    // export's node reference); one that waits on the runtime is refused, as the runtime cannot read it.
    private static void FollowExport(
        Reference reference, string field, Dictionary<string, IReadOnlyDictionary<string, EvalResult>> exports, Action<string> fail, Action<Reference> dependOn)
    {
        if (!exports.TryGetValue(reference.Target, out var requirementExports) || !requirementExports.TryGetValue(reference.Output, out var export))
        {
            fail($"field '{field}' references resource.{reference.Target}.{reference.Output}, which is not an export of a requirement of this workload.");
            return;
        }

        foreach (var inner in (export as Pending)?.References.Where(r => r.Kind == ReferenceKind.Node) ?? [])
        {
            if (inner.Target == ExpressionEvaluator.RuntimeNode)
            {
                fail($"field '{field}' references resource.{reference.Target}.{reference.Output}, which waits on the runtime; the runtime cannot read an export that waits on the runtime.");
            }
            else
            {
                dependOn(inner);
            }
        }
    }

    // After policies, a grant's target is still one pending reference to a node of its own scope or the runtime, and nothing else: the
    // evaluator makes a node reference only to a node in the scope it was given or the runtime, and a deployed part's export is a value,
    // so a policy that sets the target to a shared resource is refused here (security boundary 1). The loader checked the catalog's form.
    private void CheckGrantTarget(ResolvedNode node, string id, List<LoadError> errors)
    {
        if (!grantTargets.TryGetValue(node.Template, out var input))
        {
            errors.Add(new LoadError(id, "template", $"node '{id}': '{node.Template}' is not a grant template in the catalog's grant targets."));
            return;
        }

        if (!(node.Config.GetValueOrDefault(input) is ConfigText { Result: Pending pending }
            && ExpressionEvaluator.SingleReference(pending.Original) is { } single
            && pending.References.SetEquals([new Reference(ReferenceKind.Node, single.Node, single.Output)])))
        {
            var origin = node.Provenance.GetValueOrDefault(input);
            errors.Add(new LoadError(origin?.Source ?? id, input, $"node '{id}': {ExpressionEvaluator.GrantTargetRule(input)}"));
        }
    }

    // Every config string with its provenance key: the dotted path, an array being one leaf.
    private static IEnumerable<(string Field, ConfigText Text)> Texts(ConfigValue value, string path) => value switch
    {
        ConfigText text => [(path, text)],
        ConfigObject obj => obj.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => Texts(p.Value, path.Length == 0 ? p.Key : $"{path}.{p.Key}")),
        ConfigArray array => array.Items.SelectMany(item => Texts(item, path)),
        _ => [],
    };

    private static List<string> OrderNodes(Dictionary<string, Draft> drafts, List<LoadError> errors)
    {
        var (order, cycle) = TopologicalOrder.Sort(drafts.ToDictionary(d => d.Key, d => (IReadOnlyCollection<string>)d.Value.DependsOn));
        if (cycle is not null)
        {
            errors.Add(new LoadError("(graph)", string.Empty, $"dependency cycle: {string.Join(" -> ", cycle)}."));
        }

        return order;
    }

    private static string Hash(ResolvedNode node)
    {
        var canonical = new JObject
        {
            ["template"] = node.Template,
            ["kind"] = node.Kind.ToString().ToLowerInvariant(),
            ["config"] = ConfigJson.ToToken(new ConfigObject(node.Config)),
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString(Formatting.None))));
    }
}
