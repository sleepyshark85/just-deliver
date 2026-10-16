using System.Diagnostics;
using System.Globalization;
using jd.core.bp;
using jd.core.revisions;
using jd.resolver.graph;

namespace jd.orchestrator;

public enum IqCheckKind
{
    /// <summary>A node of the workload's graph, previewed against what is deployed: any change means the deployment drifted.</summary>
    Drift,

    /// <summary>A revision that holds traffic runs the pinned image.</summary>
    Image,
}

/// <param name="Kind">What was checked.</param>
/// <param name="Workload">The workload it belongs to.</param>
/// <param name="Subject">The node (drift), the revision (image) or, when there is none to name, the workload.</param>
/// <param name="Failure">Why it failed, naming the node or revision; empty when it passed.</param>
public sealed record IqCheck(IqCheckKind Kind, string Workload, string Subject, string Failure)
{
    public bool Passed => Failure.Length == 0;
}

/// <summary>Installation qualification: every check that ran, none left out because another failed.</summary>
public sealed record IqResult(IReadOnlyList<IqCheck> Checks, TimeSpan Elapsed)
{
    public bool Passed => Checks.All(c => c.Passed);
}

/// <summary>
/// Installation qualification of a deployed release (docs/architecture/release-record.md sections 5 and 6): the deployment still matches its
/// graphs (the preview walk reports no change anywhere) and every revision that serves traffic runs the pinned image, observed live.
/// It deploys nothing. All checks run; a failure is a failed check, never an exception.
/// </summary>
public sealed class Qualifier(Orchestrator orchestrator, IBackEndProvider provider, ITemplateStore templates, ILiveRevisions revisions)
{
    public async Task<IqResult> QualifyAsync(IReadOnlyList<ResolvedGraph> graphs, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var checks = new List<IqCheck>();
        foreach (var graph in graphs)
        {
            // Each workload is previewed on its own: a failure in one does not stop the others from being checked.
            var run = await orchestrator.PreviewAsync(graph, null, cancellationToken);
            checks.AddRange(graph.Nodes.Select(node => DriftCheck(graph, node, run.Nodes.FirstOrDefault(r => r.NodeId == node.Id))));
            checks.AddRange(await ImageChecksAsync(graph, cancellationToken));
        }

        return new IqResult(checks, Stopwatch.GetElapsedTime(started));
    }

    private static IqCheck DriftCheck(ResolvedGraph graph, GraphNode node, NodeReport? report)
    {
        var failure = report switch
        {
            null => "not checked: the preview of the workload stopped before it.",
            { Outcome: NodeOutcome.Previewed } when !DeploymentResult.ChangedOperations(report.Summary).Any() => string.Empty,
            { Outcome: NodeOutcome.Previewed } => "differs from what is deployed: " + string.Join(", ", DeploymentResult.ChangedOperations(report.Summary).Select(c => $"{c.Key} {c.Value.ToString(CultureInfo.InvariantCulture)}")),
            _ => $"cannot be previewed ({report.Outcome}): {report.Message}",
        };
        return new IqCheck(IqCheckKind.Drift, graph.Workload, node.Id, failure.Length == 0 ? failure : $"node {node.Id} {failure}");
    }

    // The runtime's id comes from its stack's state (it only identifies the app); what runs is read from the platform.
    private async Task<IEnumerable<IqCheck>> ImageChecksAsync(ResolvedGraph graph, CancellationToken cancellationToken)
    {
        IqCheck Failed(string reason) => new(IqCheckKind.Image, graph.Workload, graph.Workload, reason);
        try
        {
            var runtime = graph.Nodes.Single(n => n.Phase == Phase.Runtime);
            var idOutput = (runtime.Release ?? throw new InvalidOperationException($"node '{runtime.Id}': the runtime mapping has no release block.")).ResourceIdOutput;
            var outputs = await provider.GetOutputsAsync(runtime.Stack, templates.GetContent(runtime.Template), cancellationToken);
            if (outputs?.GetValueOrDefault(idOutput)?.Value is not { Length: > 0 } id)
            {
                return [Failed($"node {runtime.Id} has no '{idOutput}' in its state, so its revisions cannot be looked up; it was never deployed.")];
            }

            var serving = (await revisions.GetAsync(id, cancellationToken)).Where(r => r.TrafficWeight > 0).ToList();
            var pinned = graph.WorkloadImage;
            return serving.Count == 0
                ? [Failed($"no revision of {id} holds traffic.")]
                : serving.SelectMany(r => r.Images.Select(image => new IqCheck(
                    IqCheckKind.Image, graph.Workload, r.Name, image == pinned ? string.Empty : $"pinned {pinned}, live {image} on revision {r.Name}")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [Failed($"the live revisions could not be checked: {ex.Message}")];
        }
    }
}
