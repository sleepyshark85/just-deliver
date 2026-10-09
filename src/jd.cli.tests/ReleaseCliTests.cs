using Xunit;

namespace jd.cli.tests;

// `jd release` runs in-process against the sample manifest and the workloads it lists.
public sealed class ReleaseCliTests : IDisposable
{
    private static readonly string Manifest = Path.Combine(AppContext.BaseDirectory, "samples", "releases", "sample.manifest.yaml");

    private readonly string _temp = Directory.CreateTempSubdirectory("jd-release-cli-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private async Task<string> CreateAsync()
    {
        var output = Path.Combine(_temp, "set.yaml");
        var (code, _, stderr) = await RunAsync("release", "create", Manifest, "--out", output);
        Assert.Equal(0, code);
        Assert.Empty(stderr);
        return output;
    }

    [Fact]
    public async Task Create_writes_the_set_and_reports_the_deploy_order()
    {
        var output = Path.Combine(_temp, "set.yaml");

        var (code, stdout, _) = await RunAsync("release", "create", Manifest, "--out", output);

        Assert.Equal(0, code);
        Assert.Contains("release 1.0.0 with 2 workload(s), deploy order just-deliver-sample-worker, just-deliver-sample-app", stdout);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task Create_refuses_to_overwrite_an_existing_set()
    {
        var output = await CreateAsync();
        var before = await File.ReadAllTextAsync(output);

        var (code, _, stderr) = await RunAsync("release", "create", Manifest, "--out", output);

        Assert.Equal(2, code);
        Assert.Contains("already exists", stderr);
        Assert.Equal(before, await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Create_prints_validation_errors_with_the_file_and_writes_nothing()
    {
        var manifest = Path.Combine(_temp, "bad.manifest.yaml");
        await File.WriteAllTextAsync(manifest, "kind: ReleaseManifest\nlabel: 1.0.0\nworkloads:\n  - definition: ghost.yaml\n");
        var output = Path.Combine(_temp, "set.yaml");

        var (code, _, stderr) = await RunAsync("release", "create", manifest, "--out", output);

        Assert.Equal(1, code);
        Assert.StartsWith($"{manifest}: workloads[0].definition: ", stderr);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Show_prints_label_order_and_every_workload()
    {
        var output = await CreateAsync();

        var (code, stdout, _) = await RunAsync("release", "show", output);

        Assert.Equal(0, code);
        Assert.StartsWith("release 1.0.0 (created ", stdout);
        Assert.Contains("deploy order:\n  1. just-deliver-sample-worker\n  2. just-deliver-sample-app\n".ReplaceLineEndings(), stdout.ReplaceLineEndings());
        Assert.Contains("  just-deliver-sample-app", stdout);
        Assert.Contains("    team:       platform-team", stdout);
        Assert.Contains("    sha256:     ", stdout);
        Assert.Contains("    image:      ghcr.io/sleepyshark85/just-deliver-sample-app@sha256:", stdout);
        Assert.Contains("    depends on: just-deliver-sample-worker", stdout);
        Assert.Contains("    depends on: (none)", stdout);
    }

    [Fact]
    public async Task Show_of_a_modified_set_exits_1()
    {
        var output = await CreateAsync();
        await File.WriteAllTextAsync(output, (await File.ReadAllTextAsync(output)).Replace("LOG_LEVEL: info", "LOG_LEVEL: debug"));

        var (code, stdout, stderr) = await RunAsync("release", "show", output);

        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains("the release set was modified", stderr);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("release", "bogus")]
    [InlineData("release", "create", "m.yaml")]
    [InlineData("release", "create", "--out", "o.yaml")]
    [InlineData("release", "create", "m.yaml", "--out")]
    [InlineData("release", "create", "a.yaml", "b.yaml", "--out", "o.yaml")]
    [InlineData("release", "show")]
    [InlineData("release", "show", "s.yaml", "--out", "o.yaml")]
    [InlineData("release", "show", "s.yaml", "--bogus")]
    public async Task Release_usage_errors_exit_2_and_print_the_usage(params string[] args)
    {
        var (code, _, stderr) = await RunAsync(args);

        Assert.Equal(2, code);
        Assert.Contains("Usage:", stderr);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("create")]
    public async Task A_missing_input_file_exits_2(string command)
    {
        var missing = Path.Combine(_temp, "missing.yaml");
        string[] args = command == "show" ? ["release", "show", missing] : ["release", "create", missing, "--out", Path.Combine(_temp, "o.yaml")];

        var (code, _, stderr) = await RunAsync(args);

        Assert.Equal(2, code);
        Assert.Contains($"cannot read '{missing}'", stderr);
    }

    [Fact]
    public async Task Help_lists_the_release_commands()
    {
        var (_, stdout, _) = await RunAsync("--help");

        Assert.Contains("jd release create", stdout);
        Assert.Contains("jd release show", stdout);
    }
}
