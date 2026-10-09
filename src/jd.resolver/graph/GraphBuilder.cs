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
/// references, topological order, phases, hashes and the static grant check. Pure and deterministic.
/// Rules: docs/architecture/resolver.md (Output: ResolvedGraph).
/// </summary>
public sealed class GraphBuilder(EnvironmentDescriptor environment)
{
    /// <summary>Scope of nodes added by workload-scope policies; requirement ids cannot contain '@'.</summary>
    public const string WorkloadScope = "@workload";

    private sealed record Draft(string Id, string Scope, string Stack, ResolvedNode Node, List<string> DependsOn, bool DependsOnRuntime);

    public ResolvedGraph Build(PolicyResult policy)
    {
        var errors = new List<LoadError>(policy.Errors);
        var exports = policy.Requirements.OrderBy(r => r.Id, StringComparer.Ordinal).ToDictionary(r => r.Id, r => r.Exports);
        var entries = policy.Requirements.SelectMany(r => r.Nodes.Select(n => (Scope: r.Id, Node: n)))
            .Concat(policy.WorkloadNodes.Select(n => (Scope: WorkloadScope, Node: n)))
            .Select(e => (e.Scope, e.Node, Id: $"{policy.WorkloadName}/{environment.Name}/{e.Scope}/{e.Node.Name}"))
            .ToList();

        var drafts = entries
            .Select(e => Analyse(e.Scope, e.Node, e.Id, entries.Where(x => x.Scope == e.Scope).ToDictionary(x => x.Node.Name, x => x.Id), errors))
            .ToDictionary(d => d.Id);
        var phases = new Dictionary<string, Phase>();
        var nodes = new List<GraphNode>();
        foreach (var draft in TopologicalOrder(drafts, errors).Select(id => drafts[id]))
        {
            phases[draft.Id] = draft.DependsOnRuntime || draft.DependsOn.Any(d => phases[d] == Phase.AfterRuntime) ? Phase.AfterRuntime : Phase.Infrastructure;
            nodes.Add(new GraphNode(
                draft.Id, draft.Scope, draft.Node.Name, draft.Node.Template, draft.Node.Kind, draft.Stack, phases[draft.Id],
                draft.Node.Config, draft.Node.Provenance, Hash(draft.Node), draft.DependsOn, draft.DependsOnRuntime));
        }

        return new ResolvedGraph(policy.CatalogVersion, environment.Name, policy.WorkloadName, nodes, exports, errors);
    }

    // Pulumi stack names allow [A-Za-z0-9_.-]. Ids contain no '.' or '_' (names are kebab, scopes are requirement ids or
    // '@workload'), so this is one-to-one and stacks are unique by construction.
    private static string Stack(string id) => id.Replace('/', '.').Replace('@', '_');

    // Finds a node's dependencies from its pending references and applies the security check to its environment reads.
    private Draft Analyse(string scope, ResolvedNode node, string id, Dictionary<string, string> idsInScope, List<LoadError> errors)
    {
        var dependsOn = new SortedSet<string>(StringComparer.Ordinal);
        var runtime = false;
        foreach (var (field, text) in Texts(new ConfigObject(node.Config), string.Empty))
        {
            void Fail(string message) => errors.Add(new LoadError(node.Provenance.GetValueOrDefault(field)?.Source ?? id, field, $"node '{id}': {message}"));

            if (node.Kind == NodeKind.Grant)
            {
                // A grant node may use only the environment values the environment lists as grantable (D19).
                foreach (var path in text.EnvPaths.Order(StringComparer.Ordinal).Where(p => !environment.Grantable.Contains(p)))
                {
                    Fail($"grant field '{field}' uses env.{path}, which environment '{environment.Name}' does not list as grantable.");
                }
            }

            foreach (var reference in (text.Result as Pending)?.References ?? (IEnumerable<Reference>)[])
            {
                if (reference.Kind == ReferenceKind.Resource)
                {
                    Fail($"field '{field}' references resource.{reference.Target}.{reference.Output}; node config cannot reference another requirement.");
                }
                else if (reference.Target == ExpressionEvaluator.RuntimeNode)
                {
                    runtime = true;
                }
                else if (idsInScope.TryGetValue(reference.Target, out var target))
                {
                    dependsOn.Add(target);
                }
                else
                {
                    // The evaluator rejects names outside the scope it was given, which is this node's scope.
                    throw new UnreachableException($"node '{id}' references '{reference.Target}', which the evaluator should have rejected.");
                }
            }
        }

        return new Draft(id, scope, Stack(id), node, dependsOn.ToList(), runtime);
    }

    // Every config string with its provenance key: the dotted path, an array being one leaf.
    private static IEnumerable<(string Field, ConfigText Text)> Texts(ConfigValue value, string path) => value switch
    {
        ConfigText text => [(path, text)],
        ConfigObject obj => obj.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => Texts(p.Value, path.Length == 0 ? p.Key : $"{path}.{p.Key}")),
        ConfigArray array => array.Items.SelectMany(item => Texts(item, path)),
        _ => [],
    };

    // Kahn's algorithm taking the smallest ready id each time, so the order never depends on input order.
    private static List<string> TopologicalOrder(Dictionary<string, Draft> drafts, List<LoadError> errors)
    {
        var waiting = drafts.ToDictionary(d => d.Key, d => d.Value.DependsOn.Count);
        var ready = new SortedSet<string>(waiting.Where(w => w.Value == 0).Select(w => w.Key), StringComparer.Ordinal);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var id = ready.Min ?? throw new UnreachableException("the loop runs only while ready is not empty");
            ready.Remove(id);
            order.Add(id);
            foreach (var dependent in drafts.Values.Where(d => d.DependsOn.Contains(id)))
            {
                if (--waiting[dependent.Id] == 0)
                {
                    ready.Add(dependent.Id);
                }
            }
        }

        if (order.Count < drafts.Count)
        {
            // Each unordered node waits on another unordered one, so following dependencies must revisit a node: a cycle.
            var stuck = drafts.Keys.Except(order).ToHashSet();
            var path = new List<string>();
            var current = stuck.Order(StringComparer.Ordinal).First();
            while (!path.Contains(current))
            {
                path.Add(current);
                current = drafts[current].DependsOn.First(stuck.Contains);
            }

            var cycle = path.Skip(path.IndexOf(current)).Append(current);
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
