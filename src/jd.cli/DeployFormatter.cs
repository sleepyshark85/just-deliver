using System.Globalization;
using System.Text;
using jd.core.bp;
using jd.orchestrator;

namespace jd.cli;

/// <summary>The per-node lines printed by <c>jd deploy</c>: outcome, changes and time, then each changed resource.</summary>
public static class DeployFormatter
{
    public static string Format(NodeReport report)
    {
        var text = new StringBuilder();
        var changes = report.Summary.Where(s => s.Value > 0 && !string.Equals(s.Key, DeploymentResult.NoChangeOperation, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        var summary = changes.Count == 0 ? "no changes" : string.Join(", ", changes.Select(c => $"{c.Key.ToLowerInvariant()} {c.Value.ToString(CultureInfo.InvariantCulture)}"));
        text.AppendLine($"{report.NodeId}: {Describe(report.Outcome)} ({summary}, {report.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s)");
        foreach (var change in report.Changes.Where(c => !string.Equals(c.Operation, DeploymentResult.NoChangeOperation, StringComparison.OrdinalIgnoreCase)))
        {
            text.AppendLine($"  {change.Operation.ToLowerInvariant()} {change.Type}");
        }

        if (report.Message.Length > 0)
        {
            text.AppendLine($"  {report.Message}");
        }

        return text.ToString();
    }

    private static string Describe(NodeOutcome outcome) => outcome switch
    {
        NodeOutcome.Deployed => "deployed",
        NodeOutcome.Unchanged => "unchanged",
        NodeOutcome.Previewed => "previewed",
        NodeOutcome.PendingUpstream => "pending upstream",
        NodeOutcome.WaitingForRuntime => "waiting for runtime",
        NodeOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown outcome"),
    };
}
