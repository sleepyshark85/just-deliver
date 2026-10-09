using System.Runtime.CompilerServices;
using jd.definitionvalidator;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.expansion;
using jd.resolver.graph;
using jd.resolver.policies;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// Seed catalog + sample workload + a test environment -> the graph, compared with a committed snapshot, so a PR shows
// every change in what the resolver produces. Regenerate with JD_UPDATE_GOLDEN=1 and review the diff.
public class GoldenGraphTests
{
    [Fact]
    public async Task The_seed_workload_resolves_to_the_committed_graph()
    {
        var catalog = (await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog"))).Catalog!;
        var environment = (await EnvironmentFile.LoadAsync(Path.Combine(AppContext.BaseDirectory, "golden", "seed-environment.yaml"))).Descriptor!;
        var workload = (JObject)YamlSchemaValidator.ParseYaml(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml")));

        var graph = new GraphBuilder(environment).Build(new PolicyApplier(catalog, environment).Apply(new Expander(catalog, environment).Expand(workload, "workload.yaml")));

        Assert.Empty(graph.Errors);
        var actual = GraphJson.Serialize(graph);
        if (System.Environment.GetEnvironmentVariable("JD_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(SnapshotSource(), actual);
        }

        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "seed-workload.graph.json")), actual);
    }

    private static string SnapshotSource([CallerFilePath] string testFile = "") => Path.Combine(Path.GetDirectoryName(testFile)!, "golden", "seed-workload.graph.json");
}
