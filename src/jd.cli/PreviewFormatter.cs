using System.Text;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using Newtonsoft.Json;

namespace jd.cli;

/// <summary>The human-readable listing printed by <c>jd preview</c>: the graph's nodes in graph order.</summary>
public static class PreviewFormatter
{
    private const int ShortHashLength = 8;

    public static string Format(ResolvedGraph graph)
    {
        var text = new StringBuilder();
        text.AppendLine($"{graph.Workload} in {graph.Environment} (catalog {graph.CatalogVersion}): {graph.Nodes.Count} nodes");
        foreach (var node in graph.Nodes)
        {
            var dependsOn = node.DependsOn.Concat(node.DependsOnRuntime ? ["runtime"] : []).ToList();
            text.AppendLine();
            text.AppendLine(node.Id);
            text.AppendLine($"  template: {node.Template}  kind: {node.Kind}  phase: {node.Phase}");
            text.AppendLine($"  stack: {node.Stack}  hash: {node.Hash[..ShortHashLength]}");
            text.AppendLine($"  depends on: {(dependsOn.Count == 0 ? "nothing" : string.Join(", ", dependsOn))}");
            foreach (var (path, value) in ConfigLeaves.Of(string.Empty, new ConfigObject(node.Config)))
            {
                var provenance = node.Provenance.TryGetValue(path, out var source) ? source.Describe() : "no provenance";
                text.AppendLine($"  {path} = {Render(value)}   [{provenance}]");
            }
        }

        return text.ToString();
    }

    private static string Render(ConfigValue value) => value switch
    {
        ConfigText { Result: Pending pending } => $"pending: {pending.Original}",
        ConfigText { Result: Resolved resolved, EnvPaths.Count: > 0 } text => $"{resolved.Value} (from {string.Join(", ", text.EnvPaths.Order(StringComparer.Ordinal).Select(p => "env." + p))})",
        ConfigText { Result: Resolved resolved } => resolved.Value,
        _ => ConfigJson.ToToken(value).ToString(Formatting.None),
    };
}
