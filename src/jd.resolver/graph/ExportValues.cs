using jd.core.bp;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expressions;

namespace jd.resolver.graph;

/// <summary>
/// The value of each requirement export that the deployed outputs now settle: the exports are evaluated with the outputs of their
/// requirement's nodes and keyed as the references <c>${resource.&lt;id&gt;.&lt;export&gt;}</c> that read them. <see cref="Secrets"/> are the
/// references whose export read a secret output. Shared by the substrate descriptor and the orchestrator's second pass.
/// </summary>
public sealed record ExportValues(IReadOnlyDictionary<Reference, string> Values, IReadOnlySet<Reference> Secrets)
{
    /// <summary>No export is known.</summary>
    public static ExportValues None { get; } = new(new Dictionary<Reference, string>(), new HashSet<Reference>());

    // outputs: by node id. includeSecrets: whether a secret output counts as known (the descriptor drops them: a secret must never reach it).
    // file: reported in errors about an export.
    public static ExportValues Evaluate(
        ResolvedGraph graph, Catalog catalog, EnvironmentDescriptor environment,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigEntry>> outputs, bool includeSecrets, string file, ICollection<LoadError> errors)
    {
        var values = new Dictionary<Reference, string>();
        var secrets = new HashSet<Reference>();
        foreach (var (scope, exports) in graph.Exports)
        {
            var nodes = graph.Nodes.Where(n => n.Scope == scope).ToList();
            var known = new Dictionary<Reference, string>();
            var secretOutputs = new HashSet<Reference>();
            foreach (var node in nodes.Where(n => outputs.ContainsKey(n.Id)))
            {
                // An output the stack exported as null is not a value: the reference stays unresolved.
                foreach (var (output, entry) in outputs[node.Id].Where(o => o.Value.Value is not null && (includeSecrets || !o.Value.IsSecret)))
                {
                    var reference = new Reference(ReferenceKind.Node, node.Name, output);
                    known[reference] = entry.Value!;
                    if (entry.IsSecret)
                    {
                        secretOutputs.Add(reference);
                    }
                }
            }

            var evaluator = new ExpressionEvaluator(new ExpressionContext(
                catalog.Roles, catalog.Naming, environment, graph.Workload, graph.WorkloadTeam, scope, nodes.Select(n => n.Name).ToHashSet(), known,
                graph.WorkloadImage, graph.WorkloadPort));
            foreach (var (export, result) in exports)
            {
                var resolved = result is Pending pending ? evaluator.Evaluate(pending.Original, file, $"exports.{export}", errors) : result;
                if (resolved is Resolved value)
                {
                    var reference = new Reference(ReferenceKind.Resource, scope, export);
                    values[reference] = value.Value;
                    if (result is Pending read && read.References.Overlaps(secretOutputs))
                    {
                        secrets.Add(reference);
                    }
                }
            }
        }

        return new ExportValues(values, secrets);
    }
}
