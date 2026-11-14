using jd.resolver.catalog;
using jd.resolver.graph;
using Xunit;

namespace jd.orchestrator.tests;

// The release flow reads the revision suffix rule and the full traffic weight from the runtime's data (S24e), none from code.
public partial class OrchestratorTests
{
    // The graph with the runtime node's suffix rule and full weight replaced.
    private static ResolvedGraph WithRollout(ResolvedGraph graph, RevisionSuffix suffix, int fullWeight) => graph with
    {
        Nodes = [.. graph.Nodes.Select(node => node.Release is { } release ? node with { Release = release with { Suffix = suffix, FullWeight = fullWeight } } : node)],
    };

    [Fact]
    public async Task The_release_flow_makes_the_suffix_from_the_prefix_and_the_digit_counts_the_runtime_declares()
    {
        var (graph, catalog) = await ResolveAsync();
        var standard = new FakeBackend();
        var declared = new FakeBackend();

        await Create(standard, catalog).DeployAsync(graph, null, CancellationToken.None);
        await Create(declared, catalog).DeployAsync(WithRollout(graph, new RevisionSuffix("v", 4, 3), 100), null, CancellationToken.None);

        // Same config, same previous suffix: the declared rule takes 4 of the config hash's digits and 3 of the salt's that the standard rule's 8 and 6 start with.
        var reference = Parameter(RuntimeDeploys(standard)[0], "suffix");
        Assert.Matches("^r[0-9a-f]{14}$", reference);
        Assert.Equal("v" + reference[1..5] + reference[9..12], Parameter(RuntimeDeploys(declared)[0], "suffix"));
    }

    [Fact]
    public async Task The_release_flow_weighs_all_traffic_with_the_full_weight_the_runtime_declares()
    {
        var backend = new FakeBackend();
        SeedLive(backend);
        var (graph, catalog) = await ResolveAsync();

        var report = await Create(backend, catalog).DeployAsync(WithRollout(graph, new RevisionSuffix("r", 8, 6), 50), null, CancellationToken.None);

        Assert.True(report.Succeeded);
        var deploys = RuntimeDeploys(backend);
        Assert.Equal("""[{"revisionName":"app--old","weight":50}]""", Parameter(deploys[0], "traffic"));
        Assert.Equal($$"""[{"revisionName":"app--{{Parameter(deploys[0], "suffix")}}","weight":50}]""", Parameter(deploys[1], "traffic"));
    }
}
