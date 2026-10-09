using System.Runtime.CompilerServices;
using jd.definitionvalidator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using jd.resolver.policies;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// Seed catalog + sample workload + a test environment -> the graph, compared with a committed snapshot, so a PR shows
// every change in what the resolver produces. Regenerate with JD_UPDATE_GOLDEN=1 and review the diff.
public class GoldenGraphTests
{
    private const string Environment = """
        kind: Environment
        name: dev
        region: southeastasia
        tier: team
        values:
          resourceGroup: rg-dev
          cosmos: { accountName: cosmos-dev, accountId: /subscriptions/s/resourceGroups/rg-dev/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-dev, endpoint: "https://cosmos-dev.documents.azure.com:443/" }
          logAnalytics: { id: /subscriptions/s/resourceGroups/rg-dev/providers/Microsoft.OperationalInsights/workspaces/law-dev }
        grantable: [cosmos.accountId]
        """;

    [Fact]
    public async Task The_seed_workload_resolves_to_the_committed_graph()
    {
        var catalog = (await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog"))).Catalog!;
        var environment = (await EnvironmentParser.ParseAsync("dev.yaml", Environment)).Descriptor!;
        var workload = (JObject)YamlSchemaValidator.ParseYaml(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml")));

        var graph = new GraphBuilder(environment).Build(new PolicyApplier(catalog, environment).Apply(new Expander(catalog, environment).Expand(workload, "workload.yaml")));

        Assert.Empty(graph.Errors);
        var actual = Serialize(graph);
        if (System.Environment.GetEnvironmentVariable("JD_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(SnapshotSource(), actual);
        }

        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "seed-workload.graph.json")), actual);
    }

    private static string SnapshotSource([CallerFilePath] string testFile = "") => Path.Combine(Path.GetDirectoryName(testFile)!, "golden", "seed-workload.graph.json");

    private static string Serialize(ResolvedGraph graph) => new JObject
    {
        ["catalogVersion"] = graph.CatalogVersion,
        ["environment"] = graph.Environment,
        ["workload"] = graph.Workload,
        ["nodes"] = new JArray(graph.Nodes.Select(n => new JObject
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
            ["dependsOnRuntime"] = n.DependsOnRuntime,
            ["config"] = ConfigJson.ToToken(new ConfigObject(n.Config)),
            ["provenance"] = new JObject(n.Provenance.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new JProperty(p.Key, $"{p.Value.Layer}: {p.Value.Rule} ({p.Value.Source})"))),
        })),
        ["exports"] = new JObject(graph.Exports.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new JProperty(r.Key,
            new JObject(r.Value.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new JProperty(e.Key, e.Value switch
            {
                Resolved resolved => resolved.Value,
                Pending pending => pending.Original,
                _ => throw new InvalidOperationException("unknown result"),
            })))))),
    }.ToString(Formatting.Indented) + "\n";
}
