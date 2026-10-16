using System.Globalization;
using System.Text;
using jd.core.bp;
using jd.orchestrator;

namespace jd.cli;

/// <summary>The per-node lines printed by <c>jd deploy</c>: outcome, changes and time, then each changed resource and, for the runtime, each release step.</summary>
public static class DeployFormatter
{
    public static string Format(NodeReport report)
    {
        var text = new StringBuilder();
        var changes = DeploymentResult.ChangedOperations(report.Summary).OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        var summary = changes.Count == 0 ? "no changes" : string.Join(", ", changes.Select(c => $"{c.Key.ToLowerInvariant()} {c.Value.ToString(CultureInfo.InvariantCulture)}"));
        text.AppendLine($"{report.NodeId}: {Describe(report.Outcome)} ({summary}, {report.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s)");
        foreach (var change in report.Changes.Where(c => DeploymentResult.IsChange(c.Operation)))
        {
            text.AppendLine($"  {change.Operation.ToLowerInvariant()} {change.Type}");
        }

        foreach (var step in report.Steps ?? [])
        {
            text.AppendLine($"  {step.Step.ToString().ToLowerInvariant()}: {step.Outcome.ToString().ToLowerInvariant()}{(step.Detail.Length > 0 ? $" ({step.Detail})" : string.Empty)}");
        }

        // A failed release step already shows the reason.
        if (report.Message.Length > 0 && report.Steps?.Any(s => s.Outcome == StepOutcome.Failed) != true)
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
        NodeOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown outcome"),
    };
}
