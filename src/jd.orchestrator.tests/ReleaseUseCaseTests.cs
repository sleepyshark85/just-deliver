using System.Security.Cryptography;
using System.Text;
using jd.orchestrator.usecases;
using jd.resolver;
using Xunit;

namespace jd.orchestrator.tests;

// The release create and show use cases over documents only: the manifest and the set are bytes, the definitions come from an in-memory reader and
// the clock is fixed. A refusal hands back no set document, so a client has nothing to write.
public sealed class ReleaseUseCaseTests
{
    private const string ManifestName = "releases/r.manifest.yaml";
    private const string SetName = "r.set.yaml";
    private const string Pinned = "@sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private const string Manifest = "kind: ReleaseManifest\nlabel: 2.0.0\nworkloads:\n  - definition: app.yaml\n    dependsOn: [worker]\n  - definition: worker.yaml\n";

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 30, 15, 750, TimeSpan.FromHours(1));

    // The definitions the reader has, by reference; it names each one under defs/.
    private readonly Dictionary<string, byte[]> _definitions = new()
    {
        ["app.yaml"] = Definition("app", "registry.example/app" + Pinned),
        ["worker.yaml"] = Definition("worker", "registry.example/worker" + Pinned),
    };

    private static byte[] Definition(string name, string image) =>
        Encoding.UTF8.GetBytes($"apiVersion: just-deliver/v1\nkind: Workload\nmetadata:\n  name: {name}\n  team: team-a\ncontainer:\n  image: {image}\n");

    // Only the clock is reachable: creating a release reaches nothing else outside jd.
    private static Connections At(DateTimeOffset now) => new(
        () => throw new NotSupportedException(),
        () => throw new NotSupportedException(),
        () => throw new NotSupportedException(),
        () => throw new NotSupportedException(),
        () => throw new NotSupportedException(),
        () => new FixedClock(now),
        _ => throw new NotSupportedException());

    private Task<CreateReleaseResult> CreateAsync(string manifest = Manifest, DateTimeOffset? now = null) =>
        CreateRelease.RunAsync(
            new CreateReleaseInput(
                new SourceDocument(ManifestName, Encoding.UTF8.GetBytes(manifest)),
                (reference, _) => Task.FromResult(($"defs/{reference}", _definitions.TryGetValue(reference, out var bytes) ? bytes : (ReadOnlyMemory<byte>?)null))),
            At(now ?? Now),
            CancellationToken.None);

    private static Task<ShowReleaseResult> ShowAsync(ReadOnlyMemory<byte> document) =>
        ShowRelease.RunAsync(new ShowReleaseInput(new SourceDocument(SetName, document)), CancellationToken.None);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // Records compare their lists by reference, so compare the content.
    private static string Key(jd.resolver.release.ReleaseSet set) =>
        $"{set.Label}|{set.CreatedAt:O}|{string.Join(",", set.Order)}|" +
        string.Join(";", set.Workloads.Select(w => $"{w.Name}|{w.Team}|{w.DefinitionSha256}|{w.Image}|{string.Join(",", w.DependsOn)}|{w.Definition}"));

    [Fact]
    public async Task Create_stamps_the_set_at_the_connections_clock_and_its_document_shows_as_the_same_set()
    {
        var created = await CreateAsync();

        Assert.Equal((Outcome.Succeeded, 0), (created.Outcome, created.Errors.Count));
        var set = Assert.IsType<jd.resolver.release.ReleaseSet>(created.Set);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 8, 30, 15, TimeSpan.Zero), set.CreatedAt);
        Assert.Equal(["worker", "app"], set.Order);
        Assert.Equal(Hash(_definitions["app.yaml"]), set.Workloads[0].DefinitionSha256);
        var document = created.Document!.Value;
        Assert.StartsWith("kind: ReleaseSet\n", Encoding.UTF8.GetString(document.Span), StringComparison.Ordinal); // no byte-order mark: GetString would keep one

        var shown = await ShowAsync(document);

        Assert.Equal((Outcome.Succeeded, 0, "2026-11-13T08:30:15Z"), (shown.Outcome, shown.Errors.Count, shown.CreatedAt));
        Assert.Equal(Key(set), Key(shown.Set!));
    }

    [Fact]
    public async Task Show_returns_the_creation_time_as_the_set_document_holds_it()
    {
        var created = await CreateAsync(now: new DateTimeOffset(2026, 1, 2, 3, 4, 5, 999, TimeSpan.Zero));

        var shown = await ShowAsync(created.Document!.Value);

        Assert.Contains("\ncreatedAt: 2026-01-02T03:04:05Z\n", Encoding.UTF8.GetString(created.Document!.Value.Span), StringComparison.Ordinal);
        Assert.Equal("2026-01-02T03:04:05Z", shown.CreatedAt);
    }

    [Fact]
    public async Task A_definition_the_reader_has_no_content_for_is_not_found_under_the_readers_name()
    {
        var created = await CreateAsync(Manifest.Replace("worker.yaml", "ghost.yaml", StringComparison.Ordinal));

        AssertRefused(created, new LoadError(ManifestName, "workloads[1].definition", "workload definition 'defs/ghost.yaml' not found."));
    }

    [Fact]
    public async Task A_definition_that_is_not_utf8_is_refused_naming_it()
    {
        _definitions["worker.yaml"] = [.. _definitions["worker.yaml"], 0xFF];

        var created = await CreateAsync();

        AssertRefused(created, new LoadError("defs/worker.yaml", string.Empty, "not valid UTF-8."));
    }

    [Fact]
    public async Task An_image_not_pinned_by_digest_is_refused()
    {
        _definitions["app.yaml"] = Definition("app", "registry.example/app:1.0");

        var created = await CreateAsync();

        AssertRefused(
            created,
            new LoadError("defs/app.yaml", "container.image", "'registry.example/app:1.0' is not pinned by digest; a tag can change under a release. Use <image>@sha256:<64 hex digits>."));
    }

    [Fact]
    public async Task Show_refuses_a_set_whose_embedded_definition_was_modified()
    {
        var document = Encoding.UTF8.GetString((await CreateAsync()).Document!.Value.Span);
        var modified = Encoding.UTF8.GetString(_definitions["worker.yaml"]).Replace("team-a", "team-b", StringComparison.Ordinal);
        var worker = document.LastIndexOf("team: team-a", StringComparison.Ordinal); // the worker is the manifest's second workload
        var edited = string.Concat(document.AsSpan(0, worker), "team: team-b", document.AsSpan(worker + "team: team-a".Length));

        var shown = await ShowAsync(Encoding.UTF8.GetBytes(edited));

        Assert.Equal((Outcome.Failed, null, null), (shown.Outcome, shown.Set, shown.CreatedAt));
        Assert.Equal(
            [
                new LoadError(SetName, "workloads[1].definition",
                    $"does not match definitionSha256 (recorded {Hash(_definitions["worker.yaml"])}, content hashes to {Hash(Encoding.UTF8.GetBytes(modified))}); the release set was modified."),
            ],
            shown.Errors);
    }

    private static void AssertRefused(CreateReleaseResult created, LoadError error)
    {
        Assert.Equal((Outcome.Failed, null, null), (created.Outcome, created.Set, created.Document));
        Assert.Equal([error], created.Errors);
    }
}
