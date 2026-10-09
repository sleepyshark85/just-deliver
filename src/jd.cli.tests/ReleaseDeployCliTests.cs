using Xunit;

namespace jd.cli.tests;

// `jd release deploy` runs in-process against the seed catalog, the resolver's test environment and a fake backend.
// The sample set deploys the worker first, then the app (see samples/releases).
public sealed class ReleaseDeployCliTests : IDisposable
{
    private const string Worker = "just-deliver-sample-worker";
    private const string App = "just-deliver-sample-app";

    private static readonly string Samples = Path.Combine(AppContext.BaseDirectory, "samples");
    private static readonly string Environment = Path.Combine(AppContext.BaseDirectory, "golden", "seed-environment.yaml");
    private static readonly string Catalog = Path.Combine(AppContext.BaseDirectory, "catalog");

    private readonly string _temp = Directory.CreateTempSubdirectory("jd-release-deploy-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static async Task<(int Code, string Out, string Err)> RunAsync(RecordingBackend? backend, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(args, stdout, stderr, CancellationToken.None, backend);
        return (code, stdout.ToString(), stderr.ToString());
    }

    // The sample set, built from copies of the sample files so a test can change a definition first.
    private async Task<string> CreateSetAsync(Func<string, string>? changeApp = null)
    {
        Directory.CreateDirectory(Path.Combine(_temp, "workloads"));
        Directory.CreateDirectory(Path.Combine(_temp, "releases"));
        foreach (var file in new[] { "workloads/sample-worker.yaml", "releases/sample.manifest.yaml" })
        {
            File.Copy(Path.Combine(Samples, file), Path.Combine(_temp, file), overwrite: true);
        }

        var app = await File.ReadAllTextAsync(Path.Combine(Samples, "workloads", "sample-app.yaml"));
        await File.WriteAllTextAsync(Path.Combine(_temp, "workloads", "sample-app.yaml"), changeApp?.Invoke(app) ?? app);
        var set = Path.Combine(_temp, "set.yaml");
        var (code, _, stderr) = await RunAsync(null, "release", "create", Path.Combine(_temp, "releases", "sample.manifest.yaml"), "--out", set);
        Assert.True(code == 0, stderr);
        return set;
    }

    private static string[] Deploy(string set, params string[] more) => ["release", "deploy", set, "--env", Environment, "--catalog", Catalog, .. more];

    [Fact]
    public async Task Workloads_are_deployed_in_the_sets_order_with_a_heading_each_and_a_summary()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend();

        var (code, stdout, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.Equal(
            [$"deploy {Worker}.dev._workload.appinsights", $"deploy {Worker}.dev.database.container", $"deploy {App}.dev._workload.appinsights", $"deploy {App}.dev.database.container"],
            backend.Calls);
        var lines = stdout.ReplaceLineEndings("\n");
        Assert.True(lines.IndexOf($"workload {Worker}\n", StringComparison.Ordinal) < lines.IndexOf($"{Worker}/dev/database/container: deployed", StringComparison.Ordinal));
        Assert.True(lines.IndexOf($"{Worker}/dev/database/container: deployed", StringComparison.Ordinal) < lines.IndexOf($"workload {App}\n", StringComparison.Ordinal));
        Assert.Contains($"{App}/dev/database/access: waiting for runtime (no changes, ", stdout);
        Assert.Contains("release 1.0.0 on dev: 2 deployed, 0 unchanged, 0 failed, 0 not started.", stdout);
    }

    [Fact]
    public async Task A_resolution_error_in_the_second_workload_creates_nothing_and_names_the_workload_in_the_set()
    {
        var set = await CreateSetAsync(app => app.Replace("- type: database", "- type: no-such-type\n    id: database"));
        var backend = new RecordingBackend();

        var (code, stdout, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.Empty(backend.Calls);
        Assert.Empty(stdout);
        Assert.Contains($"{set}#{App}", stderr);
        Assert.Contains("no-such-type", stderr);
    }

    [Fact]
    public async Task A_failure_in_the_first_workload_stops_the_run_and_lists_the_workloads_not_started()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend { FailOn = $"{Worker}.dev._workload.appinsights" };

        var (code, stdout, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.Single(backend.Calls);
        Assert.DoesNotContain($"workload {App}", stdout);
        Assert.Contains($"jd: stopped at {Worker}, node {Worker}/dev/@workload/appinsights: provider exploded", stderr);
        Assert.Contains($"release 1.0.0 on dev: 0 deployed, 0 unchanged, 1 failed, 1 not started ({App}).", stdout);
    }

    [Fact]
    public async Task A_template_misfit_in_the_second_workload_stops_the_whole_set_before_anything_is_created()
    {
        // The app asks for a type whose mapping names a template the library does not have; the worker is fine.
        var catalog = Path.Combine(_temp, "catalog");
        CliTests.CopyDirectory(Catalog, catalog);
        await File.WriteAllTextAsync(Path.Combine(catalog, "types", "cache.yaml"), "kind: ResourceType\nname: cache\ndescription: d\nclasses: [standard]\nexports: [engine, endpoint, database, container]\n");
        await File.WriteAllTextAsync(
            Path.Combine(catalog, "mappings", "cache.yaml"),
            "kind: Mapping\nmatch: { type: cache, class: standard }\nnodes:\n  n:\n    template: azure/no-such-template\n    config: {}\nexports:\n  engine: e\n  endpoint: e\n  database: e\n  container: e\n");
        var set = await CreateSetAsync(app => app.Replace("- type: database", "- type: cache\n    id: database"));
        var backend = new RecordingBackend();

        var (code, stdout, stderr) = await RunAsync(backend, ["release", "deploy", set, "--env", Environment, "--catalog", catalog]);

        Assert.Equal(1, code);
        Assert.Empty(backend.Calls);
        Assert.Empty(stdout);
        Assert.Contains("template 'azure/no-such-template' is not in the template library", stderr);
        Assert.Contains(App, stderr);
    }

    [Fact]
    public async Task A_template_library_that_cannot_be_read_is_reported_once_for_the_whole_set()
    {
        var set = await CreateSetAsync();
        var catalog = Path.Combine(_temp, "catalog");
        CliTests.CopyDirectory(Catalog, catalog);
        Directory.Delete(Path.Combine(catalog, "templates"), recursive: true);
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, ["release", "deploy", set, "--env", Environment, "--catalog", catalog]);

        Assert.Equal(1, code);
        Assert.Empty(backend.Calls);
        Assert.Equal(1, stderr.Split("template directory not found").Length - 1);
    }

    [Fact]
    public async Task Preview_walks_the_same_order_and_creates_nothing()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend();

        var (code, stdout, _) = await RunAsync(backend, Deploy(set, "--preview"));

        Assert.Equal(0, code);
        Assert.Equal(4, backend.Calls.Count);
        Assert.All(backend.Calls, call => Assert.StartsWith("preview ", call));
        Assert.Contains(Worker, backend.Calls[0]);
        Assert.Contains(App, backend.Calls[2]);
        Assert.Contains("release 1.0.0 on dev: 2 previewed, 0 failed, 0 not started.", stdout);
    }

    [Fact]
    public async Task A_modified_set_is_refused_before_anything_is_created()
    {
        var set = await CreateSetAsync();
        await File.WriteAllTextAsync(set, (await File.ReadAllTextAsync(set)).Replace("LOG_LEVEL: info", "LOG_LEVEL: debug"));
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.Empty(backend.Calls);
        Assert.Contains("the release set was modified", stderr);
    }

    [Fact]
    public async Task Without_backend_settings_it_exits_2_and_explains()
    {
        var set = await CreateSetAsync();
        var stderr = new StringWriter();

        var code = await Cli.RunAsync(Deploy(set), new StringWriter(), stderr, CancellationToken.None, null, _ => null);

        Assert.Equal(2, code);
        Assert.Contains("jd: PULUMI_BACKEND_URL is not set", stderr.ToString());
    }

    [Theory]
    [InlineData("release", "deploy")]
    [InlineData("release", "deploy", "s.yaml", "--catalog", "c")]
    [InlineData("release", "deploy", "s.yaml", "--env", "e.yaml")]
    [InlineData("release", "deploy", "s.yaml", "--env")]
    [InlineData("release", "deploy", "a.yaml", "b.yaml", "--env", "e.yaml", "--catalog", "c")]
    [InlineData("release", "deploy", "s.yaml", "--env", "e.yaml", "--catalog", "c", "--json")]
    [InlineData("release", "deploy", "s.yaml", "--env", "e.yaml", "--catalog", "c", "--bogus")]
    public async Task Usage_errors_exit_2_and_print_the_usage(params string[] args)
    {
        var (code, _, stderr) = await RunAsync(new RecordingBackend(), args);

        Assert.Equal(2, code);
        Assert.Contains("Usage:", stderr);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("env")]
    [InlineData("catalog")]
    public async Task A_missing_input_exits_2_naming_it(string missing)
    {
        var set = await CreateSetAsync();
        var absent = Path.Combine(_temp, "absent");
        var args = new[]
        {
            "release", "deploy", missing == "set" ? absent : set,
            "--env", missing == "env" ? absent : Environment,
            "--catalog", missing == "catalog" ? absent : Catalog,
        };

        var (code, _, stderr) = await RunAsync(new RecordingBackend(), args);

        Assert.Equal(2, code);
        Assert.Contains($"cannot read '{absent}'", stderr);
    }

    [Fact]
    public async Task Help_lists_the_command()
    {
        var (_, stdout, _) = await RunAsync(null, "--help");

        Assert.Contains("jd release deploy <release-set.yaml> --env <environment.yaml> --catalog <dir> [--preview]", stdout);
    }
}
