using System.Text;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using Newtonsoft.Json;

namespace jd.cli;

/// <summary>The human-readable listing printed by <c>jd preview</c>: the graph's nodes in graph order.</summary>
internal static class PreviewFormatter
{
    private const int ShortHashLength = 8;

    public static string Format(ResolvedGraph graph)
    {
        var text = new StringBuilder();
        text.AppendLine($"{graph.Workload} in {graph.Environment} (catalog {graph.CatalogVersion}): {graph.Nodes.Count} nodes");
        foreach (var node in graph.Nodes)
        {
            text.AppendLine();
            text.AppendLine(node.Id);
            text.AppendLine($"  template: {node.Template}  kind: {node.Kind}  phase: {node.Phase}");
            text.AppendLine($"  stack: {node.Stack}  hash: {node.Hash[..ShortHashLength]}");
            if (node.Probe is { } probe)
            {
                text.AppendLine($"  probe: GET {probe.Path} expects {probe.ExpectedStatus}");
            }

            text.AppendLine($"  depends on: {(node.DependsOn.Count == 0 ? "nothing" : string.Join(", ", node.DependsOn))}");
            foreach (var leaf in node.Leaves)
            {
                text.AppendLine($"  {leaf.Path} = {Render(leaf)}   [{leaf.Provenance ?? "no provenance"}]");
            }
        }

        return text.ToString();
    }

    private static string Render(ConfigLeaf leaf) => leaf.Value switch
    {
        ConfigText { Result: Pending pending } => $"pending: {pending.Original}",
        ConfigText { Result: Resolved resolved, PartReads.Count: > 0 } text => $"{resolved.Value} (from {string.Join(", ", text.PartReads.Order(StringComparer.Ordinal))})",
        ConfigText { Result: Resolved resolved } => resolved.Value,
        _ => leaf.Json.ToString(Formatting.None),
    };
}
