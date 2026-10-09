using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.release;
using Xunit;

namespace jd.resolver.tests;

public sealed class ReleaseResolverTests
{
    private const string SetFile = "dir/set.yaml";
    private const string Digest = "registry.example/app@sha256:267b1385d6102da0d5693506e740d509a40b654e6daa2a99c1169938d182cab9";

    private static readonly EnvironmentDescriptor Env = new("dev", "region", "team", new Dictionary<string, string>(), []);

    private static async Task<Catalog> CatalogAsync()
    {
        var files = new[]
        {
            "kind: Catalog\nversion: \"1\"\n",
            "kind: ResourceType\nname: thing\ndescription: d\nclasses: [standard]\nexports: [out]\n",
            "kind: Mapping\nmatch: { type: thing }\nnodes:\n  n:\n    template: t/n\n    config: { k: 1 }\nexports:\n  out: x\n",
            TestCatalog.RuntimeMapping,
        };
        var loaded = await CatalogParser.ParseAsync(files.Select((content, i) => new CatalogSource($"f{i}.yaml", content)));
        Assert.Empty(loaded.Errors);
        return loaded.Catalog!;
    }

    private static string Definition(string name, string requirement) => $"""
        apiVersion: just-deliver/v1
        kind: Workload
        metadata:
          name: {name}
          team: team-a
        container:
          image: {Digest}
        requires:
          - type: {requirement}
        """;

    // The loader has verified the set by the time it gets here, so a set that fails to parse is built by hand.
    private static ReleaseSet Set(params (string Name, string Definition)[] workloads) =>
        new("1.0.0", DateTimeOffset.UnixEpoch, workloads.Select(w => new ReleaseWorkload(w.Name, "team-a", "sha", w.Definition, Digest, [])).ToList(), workloads.Select(w => w.Name).ToList());

    [Fact]
    public async Task A_clean_set_resolves_to_one_graph_per_workload_in_the_sets_order()
    {
        var set = Set(("worker", Definition("worker", "thing")), ("app", Definition("app", "thing")));

        var (graphs, errors) = await ReleaseResolver.ResolveAsync(SetFile, set, await CatalogAsync(), Env);

        Assert.Empty(errors);
        Assert.Equal(["worker", "app"], graphs.Select(g => g.Workload));
        Assert.All(graphs, g => Assert.Equal("dev", g.Environment));
    }

    [Fact]
    public async Task Every_failing_workload_is_reported_in_one_pass_located_in_the_set_and_no_graph_is_returned()
    {
        var set = Set(("broken", "apiVersion: just-deliver/v1\nkind: Workload\n"), ("good", Definition("good", "thing")), ("unknown", Definition("unknown", "no-such-type")));

        var (graphs, errors) = await ReleaseResolver.ResolveAsync(SetFile, set, await CatalogAsync(), Env);

        Assert.Empty(graphs);
        Assert.Contains(errors, e => e.File == $"{SetFile}#broken" && e.Message.Contains("metadata", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.File == $"{SetFile}#unknown" && e.Message.Contains("no-such-type", StringComparison.Ordinal));
        Assert.All(errors, e => Assert.NotEqual($"{SetFile}#good", e.File));
        Assert.All(errors, e => Assert.StartsWith($"{SetFile}#", e.File, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_byte_order_mark_in_a_definition_is_ignored()
    {
        var set = Set(("app", "\uFEFF" + Definition("app", "thing")));

        var (graphs, errors) = await ReleaseResolver.ResolveAsync(SetFile, set, await CatalogAsync(), Env);

        Assert.Empty(errors);
        Assert.Single(graphs);
    }
}
