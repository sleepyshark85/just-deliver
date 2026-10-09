using System.Runtime.CompilerServices;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;
using Xunit;

namespace jd.resolver.tests;

// The sample environment definitions + seed catalog -> the graph, compared with committed snapshots, like the workload golden test.
// Regenerate with JD_UPDATE_GOLDEN=1 and review the diff.
public class GoldenSubstrateTests
{
    [Fact]
    public Task The_shared_definition_resolves_to_the_committed_graph() => CheckAsync("shared", baseDescriptor: null);

    [Fact]
    public Task The_dev_definition_on_the_shared_descriptor_resolves_to_the_committed_graph() => CheckAsync("dev", baseDescriptor: "shared-environment.yaml");

    private static async Task CheckAsync(string sample, string? baseDescriptor)
    {
        var catalog = (await CatalogDirectory.LoadAsync(Path.Combine(AppContext.BaseDirectory, "catalog"))).Catalog!;
        var definition = (await EnvironmentDefinitionFile.LoadAsync(Path.Combine(AppContext.BaseDirectory, "samples", "environments", sample + ".yaml"))).Definition!;
        var baseEnvironment = baseDescriptor is null ? null : (await EnvironmentFile.LoadAsync(Path.Combine(AppContext.BaseDirectory, "golden", baseDescriptor))).Descriptor;

        var graph = Resolver.ResolveSubstrate(definition, sample + ".yaml", catalog, definition.Over("region-1", baseEnvironment));

        Assert.Empty(graph.Errors);
        var actual = GraphJson.Serialize(graph);
        var name = $"{sample}-environment.graph.json";
        if (System.Environment.GetEnvironmentVariable("JD_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(SnapshotSource(name), actual);
        }

        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", name)), actual);
    }

    private static string SnapshotSource(string name, [CallerFilePath] string testFile = "") => Path.Combine(Path.GetDirectoryName(testFile)!, "golden", name);
}
