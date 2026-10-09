using System.Diagnostics;
using jd.core.bp;
using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;

namespace jd.orchestrator;

/// <summary>
/// Walks a <see cref="ResolvedGraph"/> in graph order and provisions its infrastructure-phase nodes through the backend,
/// one at a time. Each node's pending config is evaluated again with the outputs of the nodes deployed before it, so a
/// value is never guessed. The runtime node and the nodes that depend on it are not touched. Rules: docs/architecture/provisioning.md.
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

    /// <summary>
    /// Deploys (or previews) each workload's graph in the order given and stops after the first workload that fails, so the
    /// graphs after it are never touched. <paramref name="workloadStarted"/> gets each workload's name before its nodes run.
    /// Returns the workloads handled; the last one failed when the release did.
    /// </summary>
    public async Task<IReadOnlyList<WorkloadRun>> DeployReleaseAsync(
        IReadOnlyList<ResolvedGraph> graphs, bool preview, Action<string>? workloadStarted, Action<NodeReport>? progress, CancellationToken cancellationToken)
    {
        var runs = new List<WorkloadRun>();
        foreach (var graph in graphs)
        {
            workloadStarted?.Invoke(graph.Workload);
            var run = preview ? await PreviewAsync(graph, progress, cancellationToken) : await DeployAsync(graph, progress, cancellationToken);
            runs.Add(new WorkloadRun(graph.Workload, run));
            if (!run.Succeeded)
            {
                break;
            }
        }

        return runs;
    }

    private sealed class Walk(
        IBackEndProvider provider, ITemplateStore templates, Catalog catalog, EnvironmentDescriptor environment, ResolvedGraph graph, bool preview, Action<NodeReport>? progress)
    {
        private readonly Dictionary<string, GraphNode> _byId = graph.Nodes.ToDictionary(n => n.Id);
        private readonly Dictionary<string, Dictionary<string, ConfigEntry>> _outputs = [];
        private readonly HashSet<string> _readFromState = [];

        public async Task<RunReport> RunAsync(CancellationToken cancellationToken)
        {
            // Pending config is evaluated against this catalog and environment; a graph resolved from others would deploy wrong values.
            if (graph.CatalogVersion != catalog.Version || graph.Environment != environment.Name)
            {
                throw new ArgumentException(
                    $"the graph was resolved with catalog '{graph.CatalogVersion}' and environment '{graph.Environment}', but the orchestrator has catalog '{catalog.Version}' and environment '{environment.Name}'.");
            }

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
            if (node.Phase != Phase.Infrastructure)
            {
                var message = node.Phase == Phase.Runtime ? "the runtime is deployed in a later step." : "depends on the runtime, which is deployed in a later step.";
                return Report(node, NodeOutcome.WaitingForRuntime, message);
            }

            try
            {
                var content = templates.GetContent(node.Template);
                if (preview)
                {
                    await ReadDeployedOutputsAsync(node, cancellationToken);
                }

                var errors = new List<LoadError>();
                // Only a node that reads an export (the runtime node) needs them, so an unrelated node never fails on another scope's export.
                var exported = node.Config.Values.Any(ReadsExport)
                    ? ExportValues.Evaluate(graph, catalog, environment, _outputs.ToDictionary(o => o.Key, o => (IReadOnlyDictionary<string, ConfigEntry>)o.Value), includeSecrets: true, node.Id, errors)
                    : ExportValues.None;
                var filler = new ConfigFiller(new ExpressionEvaluator(ContextFor(node, exported)), node.Id, SecretOutputs(node, exported));
                var parameters = filler.Fill(node.Config);
                errors.AddRange(filler.Errors);
                if (errors.Count > 0)
                {
                    return Report(node, NodeOutcome.Failed, string.Join(' ', errors));
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

        // The current id is the node's requirement (the node itself at workload scope), as in the first pass. Node names in scope
        // are all nodes of the node's scope; the first pass allowed only the adding policy's nodes at workload scope, but it already
        // rejected any other name, so the wider set cannot change a result here. Only the known outputs differ: node outputs are read by
        // name, so they come from the node's own scope and from the runtime; what another scope exports arrives through its exports.
        private ExpressionContext ContextFor(GraphNode node, ExportValues exported)
        {
            // What a requirement's exports are worth by now is what ${resource.<id>.<export>} reads.
            var known = new Dictionary<Reference, string>(exported.Values);
            foreach (var id in ReadableUpstream(node))
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
                known,
                graph.WorkloadImage,
                graph.WorkloadPort);
        }

        private static bool ReadsExport(ConfigValue value) => value switch
        {
            ConfigText { Result: Pending pending } => pending.References.Any(r => r.Kind == ReferenceKind.Resource),
            ConfigObject obj => obj.Properties.Values.Any(ReadsExport),
            ConfigArray array => array.Items.Any(ReadsExport),
            _ => false,
        };

        // The ids of the deployed nodes this node may read by name: its own scope's, and the runtime.
        private IEnumerable<string> ReadableUpstream(GraphNode node) =>
            node.DependsOn.Where(id => _outputs.ContainsKey(id) && (_byId[id].Scope == node.Scope || _byId[id].Name == ExpressionEvaluator.RuntimeNode));

        private HashSet<Reference> SecretOutputs(GraphNode node, ExportValues exported) =>
            ReadableUpstream(node)
                .SelectMany(id => _outputs[id].Where(o => o.Value.IsSecret).Select(o => new Reference(ReferenceKind.Node, _byId[id].Name, o.Key)))
                .Concat(exported.Secrets)
                .ToHashSet();

        // Preview deploys nothing, so what an upstream node exports is read from state, if it was deployed before.
        private async Task ReadDeployedOutputsAsync(GraphNode node, CancellationToken cancellationToken)
        {
            // Each upstream node is read once, also when it turned out not to be deployed.
            foreach (var upstream in node.DependsOn.Where(id => !_outputs.ContainsKey(id) && _readFromState.Add(id)).Select(id => _byId[id]))
            {
                var read = await provider.GetOutputsAsync(upstream.Stack, templates.GetContent(upstream.Template), cancellationToken);
                if (read is not null)
                {
                    _outputs[upstream.Id] = read;
                }
            }
        }

        private static NodeReport Report(GraphNode node, NodeOutcome outcome, string message, DeploymentResult? result = null) =>
            new(node.Id, outcome, result?.Summary ?? [], result?.Changes ?? [], message, TimeSpan.Zero,
                result?.Outputs.Where(o => !o.Value.IsSecret).ToDictionary(o => o.Key, o => o.Value) ?? []);
    }
}
