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
/// value is never guessed. The infrastructure nodes come first, then the runtime node through the release flow (dark revision, the nodes
/// that depend on the runtime, probe, traffic shift). <c>probe</c> checks a new revision is healthy before it gets traffic; <c>delay</c> waits
/// between probe attempts (<see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test replaces it). Rules: docs/architecture/provisioning.md.
/// </summary>
public sealed class Orchestrator(
    IBackEndProvider provider, ITemplateStore templates, Catalog catalog, EnvironmentDescriptor environment, IRevisionProbe probe, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>
    /// Deploys every infrastructure node, then the runtime node and the nodes that depend on it; stops at the first failure.
    /// <paramref name="progress"/> gets each node's report as it completes, the runtime's last.
    /// </summary>
    public Task<RunReport> DeployAsync(ResolvedGraph graph, Action<NodeReport>? progress, CancellationToken cancellationToken) =>
        new Walk(provider, templates, catalog, environment, probe, _delay, graph, preview: false, progress).RunAsync(cancellationToken);

    /// <summary>
    /// Previews every infrastructure node in order, then the runtime's dark revision and the nodes after the runtime (with the runtime's
    /// outputs from state); the probe is never called. Outputs of nodes the preview cannot deploy come from state
    /// (<see cref="IBackEndProvider.GetOutputsAsync"/>); a node whose references cannot be filled is reported, not previewed.
    /// </summary>
    public Task<RunReport> PreviewAsync(ResolvedGraph graph, Action<NodeReport>? progress, CancellationToken cancellationToken) =>
        new Walk(provider, templates, catalog, environment, probe, _delay, graph, preview: true, progress).RunAsync(cancellationToken);

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
            var started = Stopwatch.GetTimestamp();
            var run = preview ? await PreviewAsync(graph, progress, cancellationToken) : await DeployAsync(graph, progress, cancellationToken);
            runs.Add(new WorkloadRun(graph.Workload, run, Stopwatch.GetElapsedTime(started)));
            if (!run.Succeeded)
            {
                break;
            }
        }

        return runs;
    }

    private sealed class Walk(
        IBackEndProvider provider, ITemplateStore templates, Catalog catalog, EnvironmentDescriptor environment, IRevisionProbe probe, Func<TimeSpan, CancellationToken, Task> delay,
        ResolvedGraph graph, bool preview, Action<NodeReport>? progress)
    {
        private readonly Dictionary<string, GraphNode> _byId = graph.Nodes.ToDictionary(n => n.Id);
        private readonly Dictionary<string, Dictionary<string, ConfigEntry>> _outputs = [];
        private readonly HashSet<string> _readFromState = [];
        private readonly List<NodeReport> _reports = [];

        public async Task<RunReport> RunAsync(CancellationToken cancellationToken)
        {
            // Pending config is evaluated against this catalog and environment; a graph resolved from others would deploy wrong values.
            if (graph.CatalogVersion != catalog.Version || graph.Environment != environment.Name)
            {
                throw new ArgumentException(
                    $"the graph was resolved with catalog '{graph.CatalogVersion}' and environment '{graph.Environment}', but the orchestrator has catalog '{catalog.Version}' and environment '{environment.Name}'.");
            }

            // The runtime comes after the infrastructure it reads, whatever the topological order says; its dependants deploy inside its Grants step.
            foreach (var node in graph.Nodes.Where(n => n.Phase == Phase.Infrastructure))
            {
                if (!await RecordAsync(() => RunNodeAsync(node, cancellationToken)))
                {
                    return new RunReport(_reports);
                }
            }

            if (graph.Nodes.SingleOrDefault(n => n.Phase == Phase.Runtime) is { } runtime)
            {
                var flow = new ReleaseFlow(provider, probe, delay, preview, node => PrepareAsync(node, cancellationToken), outputs => DeployDependantsAsync(runtime, outputs, cancellationToken));
                await RecordAsync(() => flow.RunAsync(runtime, cancellationToken));
            }

            return new RunReport(_reports);
        }

        // Adds the node's report as it completes; false when it failed.
        private async Task<bool> RecordAsync(Func<Task<NodeReport>> run)
        {
            var started = Stopwatch.GetTimestamp();
            var report = await run() with { Elapsed = Stopwatch.GetElapsedTime(started) };
            _reports.Add(report);
            progress?.Invoke(report);
            return report.Outcome != NodeOutcome.Failed;
        }

        // The grants need the runtime's outputs (its principal); null when all deployed, else which node failed and why.
        private async Task<string?> DeployDependantsAsync(GraphNode runtime, Dictionary<string, ConfigEntry> runtimeOutputs, CancellationToken cancellationToken)
        {
            _outputs[runtime.Id] = runtimeOutputs;
            foreach (var node in graph.Nodes.Where(n => n.Phase == Phase.AfterRuntime))
            {
                if (!await RecordAsync(() => RunNodeAsync(node, cancellationToken)))
                {
                    return $"{node.Id}: {_reports[^1].Message}";
                }
            }

            return null;
        }

        private async Task<NodeReport> RunNodeAsync(GraphNode node, CancellationToken cancellationToken)
        {
            try
            {
                var prepared = await PrepareAsync(node, cancellationToken);
                if (prepared.Package is not { } package)
                {
                    return NodeReport.For(node, prepared.Outcome, prepared.Message);
                }

                if (preview)
                {
                    return NodeReport.For(node, NodeOutcome.Previewed, string.Empty, await provider.PreviewAsync(package, cancellationToken));
                }

                var deployed = await provider.DeployAsync(package, cancellationToken);
                _outputs[node.Id] = deployed.Outputs;
                return NodeReport.For(node, deployed.HasChanges ? NodeOutcome.Deployed : NodeOutcome.Unchanged, string.Empty, deployed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return NodeReport.For(node, NodeOutcome.Failed, ex.Message);
            }
        }

        // The node's package with its config filled from what is deployed by now, or why it cannot be filled.
        private async Task<Prepared> PrepareAsync(GraphNode node, CancellationToken cancellationToken)
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
                return new Prepared(null, NodeOutcome.Failed, string.Join(' ', errors));
            }

            if (filler.Unresolved.Count > 0)
            {
                var message = $"{string.Join("; ", filler.Unresolved)}; the upstream node has no deployed output.";
                return new Prepared(null, preview ? NodeOutcome.PendingUpstream : NodeOutcome.Failed, message);
            }

            return new Prepared(new DeploymentPackage { StackName = node.Stack, DeploymentContent = content, DeploymentParameters = parameters });
        }

        // The current id is the node's requirement (the node itself at workload scope), as in the first pass. Node names in scope
        // are all nodes of the node's scope; the first pass allowed only the adding policy's nodes at workload scope, but it already
        // rejected any other name, so the wider set cannot change a result here. Only the known outputs differ: node outputs are read by
        // name, so they come from the node's own scope and from the runtime; what another scope exports arrives through its exports. An
        // environment run's parts read each other in place, as in the first pass (the graph's Parts).
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
                graph.WorkloadPort,
                graph.Parts);
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
    }
}
