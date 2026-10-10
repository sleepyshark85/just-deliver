using jd.resolver.expansion;
using jd.resolver.expressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace jd.resolver.graph;

/// <summary>
/// The stable JSON form of a <see cref="ResolvedGraph"/>, part of the graph contract (the committed golden snapshot pins it):
/// nodes in graph order, objects with keys in ordinal order, a pending value as its original text.
/// </summary>
public static class GraphJson
{
    public static string Serialize(ResolvedGraph graph) => new JObject
    {
        ["catalogVersion"] = graph.CatalogVersion,
        ["environment"] = graph.Environment,
        ["workload"] = graph.Workload,
        ["nodes"] = new JArray(graph.Nodes.Select(Node)),
        ["exports"] = new JObject(graph.Exports.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new JProperty(r.Key,
            new JObject(r.Value.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new JProperty(e.Key, e.Value switch
            {
                Resolved resolved => resolved.Value,
                Pending pending => pending.Original,
                _ => throw new InvalidOperationException("unknown result"),
            })))))),
    }.ToString(Formatting.Indented) + "\n";

    private static JObject Node(GraphNode n)
    {
        var node = new JObject
        {
            ["id"] = n.Id,
            ["scope"] = n.Scope,
            ["name"] = n.Name,
            ["template"] = n.Template,
            ["kind"] = n.Kind.ToString(),
            ["stack"] = n.Stack,
            ["phase"] = n.Phase.ToString(),
            ["hash"] = n.Hash,
            ["dependsOn"] = new JArray(n.DependsOn),
            ["config"] = ConfigJson.ToToken(new ConfigObject(n.Config)),
            ["provenance"] = new JObject(n.Provenance.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new JProperty(p.Key, p.Value.Describe()))),
        };
        if (n.Probe is { } probe)
        {
            node["probe"] = new JObject
            {
                ["path"] = probe.Path,
                ["expectedStatus"] = probe.ExpectedStatus,
                ["timeoutSeconds"] = probe.TimeoutSeconds,
                ["intervalSeconds"] = probe.IntervalSeconds,
            };
        }

        if (n.Release is { } release)
        {
            node["release"] = new JObject
            {
                ["suffixInput"] = release.SuffixInput,
                ["trafficInput"] = release.TrafficInput,
                ["trafficOutput"] = release.TrafficOutput,
                ["revisionOutput"] = release.RevisionOutput,
                ["fqdnOutput"] = release.FqdnOutput,
                ["revisionKey"] = release.RevisionKey,
                ["latestKey"] = release.LatestKey,
                ["weightKey"] = release.WeightKey,
            };
        }

        return node;
    }
}
