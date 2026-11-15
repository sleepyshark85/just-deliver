using jd.resolver.release;
using Xunit;

namespace jd.resolver.tests;

// The binder reads the release manifest and the release set by the release.* roles of their shipped schemas (docs/architecture/documents.md
// section 5): each value with where the document holds it, as the release rules locate their errors.
public sealed class BinderReleaseReadTests
{
    private static async Task<ReleaseDocument> ReadAsync(string kind, string yaml)
    {
        var index = await SchemaIndex.Shipped;
        var loaded = await DocumentLoader.LoadAsync("release.yaml", yaml, [kind], CancellationToken.None);
        Assert.Empty(loaded.Errors);
        return new Binder(index).ReadRelease(loaded.Value!);
    }

    [Fact]
    public async Task A_manifest_reads_each_workload_with_where_its_definition_path_and_dependencies_are_held()
    {
        var release = await ReadAsync("ReleaseManifest", "kind: ReleaseManifest\nlabel: 2.0.0\nworkloads:\n  - definition: app.yaml\n    dependsOn: [worker]\n  - definition: worker.yaml\n");

        Assert.Equal(("2.0.0", string.Empty, "workloads"), (release.Label, release.CreatedAt, release.WorkloadsLocation));
        Assert.Collection(
            release.Workloads,
            app =>
            {
                Assert.Equal(("app.yaml", string.Empty, string.Empty), (app.DefinitionPath, app.Definition, app.DefinitionSha256));
                Assert.Equal(("workloads[0]", "workloads[0].definition"), (app.Location, app.DefinitionLocation));
                Assert.Equal(["worker"], app.DependsOn);
                Assert.Equal(["workloads[0].dependsOn[0]"], app.DependsOnLocations);
            },
            worker =>
            {
                Assert.Equal(("worker.yaml", "workloads[1]", "workloads[1].definition"), (worker.DefinitionPath, worker.Location, worker.DefinitionLocation));
                Assert.Empty(worker.DependsOn);
                Assert.Empty(worker.DependsOnLocations);
            });
    }

    [Fact]
    public async Task A_set_reads_its_creation_time_and_each_embedded_definition_with_the_hash_that_pins_it()
    {
        var sha256 = new string('a', 64);
        var release = await ReadAsync(
            "ReleaseSet",
            $"kind: ReleaseSet\nlabel: 1.0.0\ncreatedAt: 2026-11-15T10:20:30Z\nworkloads:\n  - definitionSha256: {sha256}\n    dependsOn: []\n    definition: \"kind: Workload\\n\"\n");

        Assert.Equal(("1.0.0", "2026-11-15T10:20:30Z", "createdAt"), (release.Label, release.CreatedAt, release.CreatedAtLocation));
        var entry = Assert.Single(release.Workloads);
        Assert.Equal((string.Empty, "kind: Workload\n", sha256), (entry.DefinitionPath, entry.Definition, entry.DefinitionSha256));
        Assert.Equal("workloads[0].definition", entry.DefinitionLocation);
        Assert.Empty(entry.DependsOn);
    }
}
