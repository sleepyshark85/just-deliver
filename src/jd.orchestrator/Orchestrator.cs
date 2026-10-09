using System.Diagnostics;
using jd.core.bp;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expressions;
using jd.resolver.graph;

namespace jd.orchestrator;

/// <summary>
/// Walks a <see cref="ResolvedGraph"/> in graph order and provisions its infrastructure-phase nodes through the backend,
/// one at a time. Each node's pending config is evaluated again with the outputs of the nodes deployed before it, so a
/// value is never guessed. Nodes that depend on the runtime are not touched. Rules: docs/architecture/provisioning.md.
/// </summary>
public sealed class Orchestrator(IBackEndProvider provider, ITemplateStore templates, Catalog catalog, EnvironmentDescriptor environment)
{
    /// <summary>Deploys every infrastructure node; stops at the first failure. <paramref name="progress"/> gets each node's report as it completes.</summary>
    public Task<RunReport> DeployAsync(ResolvedGraph graph, Action<NodeReport>? progress, CancellationToken cancellationToken) =>
        new Walk(provider, templates, catalog, environment, graph, preview: false, progress).RunAsync(cancellationToken);

    /// <summary>
    /// Previews every infrastructure node in order. Outputs of nodes the preview cannot deploy come from state
    /// (<see cref="IBackEndProvider.GetOutputsAsync"/>); a node whose references cannot be filled is reported, not previewed.
    /// </summary>
    public Task<RunReport> PreviewAsync(ResolvedGraph graph, Action<NodeReport>? progress, CancellationToken cancellationToken) =>
        new Walk(provider, templates, catalog, environment, graph, preview: true, progress).RunAsync(cancellationToken);

    private sealed class Walk(
        IBackEndProvider provider, ITemplateStore templates, Catalog catalog, EnvironmentDescriptor environment, ResolvedGraph graph, bool preview, Action<NodeReport>? progress)
    {
        private readonly Dictionary<string, GraphNode> _byId = graph.Nodes.ToDictionary(n => n.Id);
        private readonly Dictionary<string, Dictionary<string, ConfigEntry>> _outputs = [];

        public async Task<RunReport> RunAsync(CancellationToken cancellationToken)
        {
            var reports = new List<NodeReport>();
            foreach (var node in graph.Nodes)
            {
                var started = Stopwatch.GetTimestamp();
                var report = await RunNodeAsync(node, cancellationToken) with { Elapsed = Stopwatch.GetElapsedTime(started) };
                reports.Add(report);
                progress?.Invoke(report);
                if (report.Outcome == NodeOutcome.Failed)
                {
                    break;
                }
            }

            return new RunReport(reports);
        }

        private async Task<NodeReport> RunNodeAsync(GraphNode node, CancellationToken cancellationToken)
        {
            if (node.Phase == Phase.AfterRuntime)
            {
                return Report(node, NodeOutcome.WaitingForRuntime, "depends on the runtime, which is deployed in a later step.");
            }

            try
            {
                var content = templates.GetContent(node.Template);
                if (preview)
                {
                    await ReadDeployedOutputsAsync(node, cancellationToken);
                }

                var filler = new ConfigFiller(new ExpressionEvaluator(ContextFor(node)), node.Id, SecretOutputs(node));
                var parameters = filler.Fill(node.Config);
                if (filler.Errors.Count > 0)
                {
                    return Report(node, NodeOutcome.Failed, string.Join(' ', filler.Errors));
                }

                if (filler.Unresolved.Count > 0)
                {
                    var message = $"{string.Join("; ", filler.Unresolved)}; the upstream node has no deployed output.";
                    return Report(node, preview ? NodeOutcome.PendingUpstream : NodeOutcome.Failed, message);
                }

                var package = new DeploymentPackage { StackName = node.Stack, DeploymentContent = content, DeploymentParameters = parameters };
                if (preview)
                {
                    var previewed = await provider.PreviewAsync(package, cancellationToken);
                    return Report(node, NodeOutcome.Previewed, string.Empty, previewed);
                }

                var deployed = await provider.DeployAsync(package, cancellationToken);
                _outputs[node.Id] = deployed.Outputs;
                return Report(node, deployed.HasChanges ? NodeOutcome.Deployed : NodeOutcome.Unchanged, string.Empty, deployed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Report(node, NodeOutcome.Failed, ex.Message);
            }
        }

        // Same rules as the first pass: the node's scope is its requirement (or the node itself at workload scope), and the
        // nodes in scope are those of that scope. Only the known outputs differ.
        private ExpressionContext ContextFor(GraphNode node)
        {
            var known = new Dictionary<Reference, string>();
            foreach (var id in node.DependsOn.Where(_outputs.ContainsKey))
            {
                foreach (var (output, entry) in _outputs[id])
                {
                    // An output the stack exported as null is not a value: the reference stays unresolved.
                    if (entry.Value is not null)
                    {
                        known[new Reference(ReferenceKind.Node, _byId[id].Name, output)] = entry.Value;
                    }
                }
            }

            return new ExpressionContext(
                catalog.Roles,
                catalog.Naming,
                environment,
                graph.Workload,
                graph.WorkloadTeam,
                node.Scope == GraphBuilder.WorkloadScope ? node.Name : node.Scope,
                graph.Nodes.Where(n => n.Scope == node.Scope).Select(n => n.Name).ToHashSet(),
                known);
        }

        private HashSet<Reference> SecretOutputs(GraphNode node) =>
            node.DependsOn.Where(_outputs.ContainsKey)
                .SelectMany(id => _outputs[id].Where(o => o.Value.IsSecret).Select(o => new Reference(ReferenceKind.Node, _byId[id].Name, o.Key)))
                .ToHashSet();

        // Preview deploys nothing, so what an upstream node exports is read from state, if it was deployed before.
        private async Task ReadDeployedOutputsAsync(GraphNode node, CancellationToken cancellationToken)
        {
            foreach (var upstream in node.DependsOn.Where(id => !_outputs.ContainsKey(id)).Select(id => _byId[id]))
            {
                var read = await provider.GetOutputsAsync(upstream.Stack, templates.GetContent(upstream.Template), cancellationToken);
                if (read is not null)
                {
                    _outputs[upstream.Id] = read;
                }
            }
        }

        private static NodeReport Report(GraphNode node, NodeOutcome outcome, string message, DeploymentResult? result = null) =>
            new(node.Id, outcome, result?.Summary ?? [], result?.Changes ?? [], message, TimeSpan.Zero);
    }
}
