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
            foreach (var (path, value) in Leaves(string.Empty, node.Config))
            {
                var provenance = node.Provenance.TryGetValue(path, out var source) ? source.Describe() : "no provenance";
                text.AppendLine($"  {path} = {Render(value)}   [{provenance}]");
            }
        }

        return text.ToString();
    }

    // Leaf fields by dotted path, the keys of Provenance; an array is one leaf.
    private static IEnumerable<(string Path, ConfigValue Value)> Leaves(string prefix, IReadOnlyDictionary<string, ConfigValue> properties) =>
        properties.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p =>
        {
            var path = prefix.Length == 0 ? p.Key : $"{prefix}.{p.Key}";
            return p.Value is ConfigObject nested ? Leaves(path, nested.Properties) : [(path, p.Value)];
        });

    private static string Render(ConfigValue value) => value switch
    {
        ConfigText { Result: Pending pending } => $"pending: {pending.Original}",
        ConfigText { Result: Resolved resolved } => resolved.Value,
        _ => ConfigJson.ToToken(value).ToString(Formatting.None),
    };
}
