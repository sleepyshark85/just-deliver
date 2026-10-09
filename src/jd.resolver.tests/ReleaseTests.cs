using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using jd.resolver.release;
using Xunit;

namespace jd.resolver.tests;

public sealed class ReleaseTests : IDisposable
{
    private const string Digest = "registry.example/app@sha256:267b1385d6102da0d5693506e740d509a40b654e6daa2a99c1169938d182cab9";

    private static readonly DateTimeOffset Created = new(2026, 10, 9, 12, 30, 15, 987, TimeSpan.FromHours(2));

    private readonly string _temp = Directory.CreateTempSubdirectory("jd-release-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static string Definition(string name, string image = Digest) => $"""
        apiVersion: just-deliver/v1
        kind: Workload
        metadata:
          name: {name}
          team: team-a
        container:
          image: {image}
        """;

    // Writes <name>.yaml for each workload (name, image) and a manifest listing them with their dependsOn.
    private string WriteManifest(params (string Name, string[] DependsOn, string Image)[] workloads)
    {
        var manifest = new StringBuilder("kind: ReleaseManifest\nlabel: 2.1.0-rc.1\nworkloads:\n");
        foreach (var (name, dependsOn, image) in workloads)
        {
            File.WriteAllText(Path.Combine(_temp, $"{name}.yaml"), Definition(name, image));
            manifest.Append($"  - definition: {name}.yaml\n    dependsOn: [{string.Join(", ", dependsOn)}]\n");
        }

        var path = Path.Combine(_temp, "manifest.yaml");
        File.WriteAllText(path, manifest.ToString());
        return path;
    }

    private string WriteManifest(params (string Name, string[] DependsOn)[] workloads) =>
        WriteManifest(workloads.Select(w => (w.Name, w.DependsOn, Digest)).ToArray());

    private static async Task<ReleaseSet> BuildAsync(string manifest)
    {
        var result = await ReleaseBuilder.BuildAsync(manifest, Created);
        Assert.Empty(result.Errors);
        return result.Set!;
    }

    private static async Task<IReadOnlyList<string>> ErrorsAsync(string manifest) =>
        (await ReleaseBuilder.BuildAsync(manifest, Created)).Errors.Select(e => e.ToString()).ToList();

    [Fact]
    public async Task A_manifest_becomes_a_set_with_label_time_and_workloads()
    {
        var manifest = WriteManifest(("web-app", ["web-db"]), ("web-db", []));

        var set = await BuildAsync(manifest);

        Assert.Equal("2.1.0-rc.1", set.Label);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 10, 30, 15, TimeSpan.Zero), set.CreatedAt);
        var web = Assert.Single(set.Workloads, w => w.Name == "web-app");
        Assert.Equal("team-a", web.Team);
        Assert.Equal(Digest, web.Image);
        Assert.Equal(["web-db"], web.DependsOn);
        Assert.Equal(Definition("web-app"), web.Definition);
    }

    [Fact]
    public async Task A_manifest_that_breaks_its_schema_is_reported_with_the_file()
    {
        var path = Path.Combine(_temp, "manifest.yaml");
        File.WriteAllText(path, "kind: ReleaseManifest\nlabel: v1\nworkloads: []\n");

        var errors = await ErrorsAsync(path);

        Assert.Contains(errors, e => e.StartsWith($"{path}: label: ", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith($"{path}: workloads: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invalid_workload_definition_is_reported_at_its_own_file()
    {
        var manifest = WriteManifest(("web-app", []));
        File.WriteAllText(Path.Combine(_temp, "web-app.yaml"), "kind: Workload\n");

        var errors = await ErrorsAsync(manifest);

        Assert.Contains(errors, e => e.StartsWith($"{Path.Combine(_temp, "web-app.yaml")}: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_definition_is_reported_at_the_manifest_entry()
    {
        var manifest = WriteManifest(("web-app", []));
        File.Delete(Path.Combine(_temp, "web-app.yaml"));

        var error = Assert.Single(await ErrorsAsync(manifest));

        Assert.StartsWith($"{manifest}: workloads[0].definition: ", error);
    }

    [Theory]
    [InlineData("registry.example/app:1.2.3")]
    [InlineData("registry.example/app")]
    [InlineData("registry.example/app@sha256:abc")]
    public async Task An_image_not_pinned_by_digest_is_rejected(string image)
    {
        var manifest = WriteManifest(("web-app", [], image));

        var error = Assert.Single(await ErrorsAsync(manifest));

        Assert.StartsWith($"{Path.Combine(_temp, "web-app.yaml")}: container.image: ", error);
        Assert.Contains("not pinned by digest", error);
    }

    [Fact]
    public async Task Duplicate_workload_names_are_rejected()
    {
        var manifest = WriteManifest(("web-app", []), ("web-db", []));
        File.WriteAllText(Path.Combine(_temp, "web-db.yaml"), Definition("web-app"));

        var error = Assert.Single(await ErrorsAsync(manifest));

        Assert.Equal($"{manifest}: workloads[1].definition: workload name 'web-app' is already used by workloads[0]; names must be unique within a release.", error);
    }

    [Fact]
    public async Task A_dependency_on_a_workload_outside_the_manifest_is_rejected()
    {
        var manifest = WriteManifest(("web-app", ["ghost"]));

        var error = Assert.Single(await ErrorsAsync(manifest));

        Assert.Equal($"{manifest}: workloads[0].dependsOn[0]: 'ghost' is not a workload in this manifest.", error);
    }

    [Fact]
    public async Task A_workload_cannot_depend_on_itself()
    {
        var manifest = WriteManifest(("web-app", ["web-app"]));

        var error = Assert.Single(await ErrorsAsync(manifest));

        Assert.Equal($"{manifest}: workloads[0].dependsOn[0]: 'web-app' cannot depend on itself.", error);
    }

    [Fact]
    public async Task A_cycle_is_rejected_and_names_its_members_only()
    {
        var manifest = WriteManifest(("aaa-free", []), ("bbb-one", ["ccc-two"]), ("ccc-two", ["ddd-three"]), ("ddd-three", ["bbb-one"]));

        var error = Assert.Single(await ErrorsAsync(manifest));

        Assert.Equal($"{manifest}: workloads: dependency cycle: bbb-one -> ccc-two -> ddd-three -> bbb-one (each depends on the next).", error);
    }

    [Fact]
    public async Task Dependencies_deploy_first_and_ties_break_by_name()
    {
        // beta, gamma and zeta are free and go first by name; alpha waits for zeta.
        var manifest = WriteManifest(("zeta", []), ("alpha", ["zeta"]), ("gamma", []), ("beta", []));

        var set = await BuildAsync(manifest);

        Assert.Equal(["beta", "gamma", "zeta", "alpha"], set.Order);
    }

    [Fact]
    public async Task Collected_errors_include_every_definition_problem()
    {
        var manifest = WriteManifest(("web-app", [], "registry.example/app:1"), ("web-db", [], "registry.example/db:2"));

        Assert.Equal(2, (await ErrorsAsync(manifest)).Count);
    }

    // Nothing may be normalised on the way into the set and back: line endings, blank and indented first lines,
    // trailing white space, non-ASCII text, a missing final newline.
    [Theory]
    [InlineData("", "\r\n", "   \r\n# café\t")]
    [InlineData("", "\n", "\n\n")]
    [InlineData("", "\n", "")]
    [InlineData("\n\n# lead\n", "\n", "\n")]
    [InlineData("   \n", "\n", "\n")]
    [InlineData("", "\n", "\n# trailing \n")]
    public async Task The_hash_covers_the_exact_bytes_and_survives_the_round_trip(string prefix, string newline, string suffix)
    {
        var bytes = Encoding.UTF8.GetBytes(prefix + Definition("web-app").Replace("\n", newline) + suffix);
        var manifest = WriteManifest(("web-app", []));
        await File.WriteAllBytesAsync(Path.Combine(_temp, "web-app.yaml"), bytes);

        var set = await BuildAsync(manifest);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), set.Workloads[0].DefinitionSha256);
        var path = Path.Combine(_temp, "set.yaml");
        await ReleaseSetFile.WriteAsync(set, path);
        var loaded = await ReleaseSetFile.LoadAsync(path);
        Assert.Empty(loaded.Errors);
        Assert.Equal(Key(set), Key(loaded.Set!));
    }

    [Fact]
    public async Task Loading_a_modified_definition_is_an_error()
    {
        var set = await BuildAsync(WriteManifest(("web-app", []), ("web-db", [])));
        var path = Path.Combine(_temp, "set.yaml");
        await ReleaseSetFile.WriteAsync(set, path);
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("team: team-a", "team: team-b"));

        var errors = (await ReleaseSetFile.LoadAsync(path)).Errors.Select(e => e.ToString()).ToList();

        // Both embedded copies changed, so both are caught, each at its own entry.
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Contains("the release set was modified", e));
        Assert.StartsWith($"{path}: workloads[0].definition: ", errors[0]);
        Assert.StartsWith($"{path}: workloads[1].definition: ", errors[1]);
    }

    [Fact]
    public async Task A_release_set_is_never_overwritten()
    {
        var set = await BuildAsync(WriteManifest(("web-app", [])));
        var path = Path.Combine(_temp, "set.yaml");
        await ReleaseSetFile.WriteAsync(set, path);
        var before = await File.ReadAllTextAsync(path);

        await Assert.ThrowsAsync<IOException>(() => ReleaseSetFile.WriteAsync(set with { Label = "9.9.9" }, path));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Loading_a_set_that_is_not_there_or_breaks_its_schema_is_an_error()
    {
        Assert.Single((await ReleaseSetFile.LoadAsync(Path.Combine(_temp, "none.yaml"))).Errors);
        var path = Path.Combine(_temp, "set.yaml");
        await File.WriteAllTextAsync(path, "kind: ReleaseSet\nlabel: 1.0.0\n");

        Assert.NotEmpty((await ReleaseSetFile.LoadAsync(path)).Errors);
    }

    [Fact]
    public async Task The_sample_manifest_creates_the_committed_set()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "samples", "releases", "sample.manifest.yaml");
        var set = await BuildAsync(manifest);
        var path = Path.Combine(_temp, "sample.release-set.yaml");
        await ReleaseSetFile.WriteAsync(set with { CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) }, path);

        var actual = await File.ReadAllTextAsync(path);
        if (System.Environment.GetEnvironmentVariable("JD_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(SnapshotSource(), actual);
        }

        // The app depends on the worker, so the worker deploys first although the app sorts first by name.
        Assert.Equal(["just-deliver-sample-worker", "just-deliver-sample-app"], set.Order);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "sample.release-set.yaml")), actual);
        Assert.Empty((await ReleaseSetFile.LoadAsync(path)).Errors);
    }

    private static string SnapshotSource([CallerFilePath] string testFile = "") => Path.Combine(Path.GetDirectoryName(testFile)!, "golden", "sample.release-set.yaml");

    // Records compare their lists by reference, so compare the content.
    private static string Key(ReleaseSet set) =>
        $"{set.Label}|{set.CreatedAt:O}|{string.Join(",", set.Order)}|" +
        string.Join(";", set.Workloads.Select(w => $"{w.Name}|{w.Team}|{w.DefinitionSha256}|{w.Image}|{string.Join(",", w.DependsOn)}|{w.Definition}"));
}
