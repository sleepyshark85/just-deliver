using jd.core.identity;
using jd.core.revisions;
using Xunit;

namespace jd.cli.tests;

// `jd release deploy` runs in-process against the seed catalog, the resolver's test environment, a fake backend, a fake principal and a
// clock that moves one second per reading. The sample set deploys the worker first, then the app (see samples/releases).
// The release record's tests are in ReleaseDeployCliTests.Records.cs.
public sealed partial class ReleaseDeployCliTests : IDisposable
{
    private const string Worker = "just-deliver-sample-worker";
    private const string App = "just-deliver-sample-app";

    private static readonly string Samples = Path.Combine(AppContext.BaseDirectory, "samples");
    private static readonly string Environment = Path.Combine(AppContext.BaseDirectory, "golden", "seed-environment.yaml");
    private static readonly string Catalog = Path.Combine(AppContext.BaseDirectory, "catalog");

    private readonly string _temp = Directory.CreateTempSubdirectory("jd-release-deploy-tests").FullName;
    private readonly FakeIdentity _identity = new();
    private IDeployerIdentity? _port;
    private StepClock _clock = new();
    private readonly FakeLiveRevisions _live = new();
    private ILiveRevisions? _reader; // what the run is given; null makes it build its own from the ARM_* variables

    // JD_RECORDS_DIR as the run sees it: the temp records directory unless a test changes it.
    private readonly string _records;
    private string? _recordsVariable;

    // Other variables a run sees (the ARM_* ones).
    private readonly Dictionary<string, string> _variables = [];

    public ReleaseDeployCliTests()
    {
        _port = _identity;
        _reader = _live;
        Directory.CreateDirectory(_records = Path.Combine(_temp, "records"));
        _recordsVariable = _records;
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private async Task<(int Code, string Out, string Err)> RunAsync(RecordingBackend? backend, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(args, stdout, stderr, CancellationToken.None, backend, name => name == "JD_RECORDS_DIR" ? _recordsVariable : _variables.GetValueOrDefault(name), backend, _port, _clock, _reader);
        return (code, stdout.ToString(), stderr.ToString());
    }

    // The sample set, built from copies of the sample files so a test can change a definition first.
    private async Task<string> CreateSetAsync(Func<string, string>? changeApp = null, string fileName = "set.yaml", string label = "1.0.0")
    {
        Directory.CreateDirectory(Path.Combine(_temp, "workloads"));
        Directory.CreateDirectory(Path.Combine(_temp, "releases"));
        foreach (var file in new[] { "workloads/sample-worker.yaml", "releases/sample.manifest.yaml" })
        {
            File.Copy(Path.Combine(Samples, file), Path.Combine(_temp, file), overwrite: true);
        }

        var manifest = Path.Combine(_temp, "releases", "sample.manifest.yaml");
        await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest)).Replace("label: 1.0.0", $"label: {label}", StringComparison.Ordinal));
        var app = await File.ReadAllTextAsync(Path.Combine(Samples, "workloads", "sample-app.yaml"));
        await File.WriteAllTextAsync(Path.Combine(_temp, "workloads", "sample-app.yaml"), changeApp?.Invoke(app) ?? app);
        var set = Path.Combine(_temp, fileName);
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
        // Per workload: its infrastructure, its runtime, the grants (inside the runtime's Grants step), then the probe of the new revision and the shift.
        var walk = backend.Calls.Where(c => !c.StartsWith("preview ", StringComparison.Ordinal)).ToList(); // the IQ after it only previews
        Assert.Equal(14, walk.Count);
        foreach (var (name, first) in new[] { (Worker, 0), (App, 7) })
        {
            Assert.Equal([$"deploy {name}.dev.database.container", $"deploy {name}.dev.monitoring.appinsights", $"deploy {name}.dev._workload.runtime"], walk.Skip(first).Take(3));
            Assert.Equal([$"deploy {name}.dev.database.access", $"deploy {name}.dev.monitoring.access"], walk.Skip(first + 3).Take(2).Order(StringComparer.Ordinal));
            Assert.Equal("probe out-latestRevisionFqdn/health", walk[first + 5]);
            Assert.Equal($"deploy {name}.dev._workload.runtime", walk[first + 6]); // the shift
        }

        var lines = stdout.ReplaceLineEndings("\n");
        Assert.True(lines.IndexOf($"workload {Worker}\n", StringComparison.Ordinal) < lines.IndexOf($"{Worker}/dev/database/container: deployed", StringComparison.Ordinal));
        Assert.True(lines.IndexOf($"{Worker}/dev/database/container: deployed", StringComparison.Ordinal) < lines.IndexOf($"workload {App}\n", StringComparison.Ordinal));
        Assert.Contains($"{App}/dev/database/access: deployed (create 1, ", stdout);
        Assert.Contains($"{App}/dev/@workload/runtime: deployed (create 1, ", stdout);
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
        var backend = new RecordingBackend { FailOn = $"{Worker}.dev.database.container" };

        var (code, stdout, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal(1, code);
        Assert.Single(backend.Calls);
        Assert.DoesNotContain($"workload {App}", stdout);
        Assert.Contains($"jd: stopped at {Worker}, node {Worker}/dev/database/container: provider exploded", stderr);
        Assert.Contains($"release 1.0.0 on dev: 0 deployed, 0 unchanged, 1 failed, 1 not started ({App}).", stdout);
    }

    [Fact]
    public async Task A_template_misfit_in_the_second_workload_stops_the_whole_set_before_anything_is_created()
    {
        // The app asks for a component whose class names a template the library does not have; the worker is fine.
        var catalog = Path.Combine(_temp, "catalog");
        CliTests.CopyDirectory(Catalog, catalog);
        await File.WriteAllTextAsync(
            Path.Combine(catalog, "components", "cache.yaml"),
            "kind: Component\nname: cache\ndescription: d\nexports: [engine, endpoint, database, container]\nimplementations:\n  default:\n    classes:\n      standard:\n"
            + "        nodes:\n          n:\n            template: azure/no-such-template\n            config: {}\n        exports: { engine: e, endpoint: e, database: e, container: e }\n");
        var set = await CreateSetAsync(app => app.Replace("- type: database", "- type: cache\n    id: database"));
        var environment = Path.Combine(_temp, "dev.env.yaml");
        await File.WriteAllTextAsync(environment, (await File.ReadAllTextAsync(Environment)).Replace("offers: [container-app, database, monitoring]", "offers: [container-app, database, monitoring, cache]"));
        var backend = new RecordingBackend();

        var (code, stdout, stderr) = await RunAsync(backend, ["release", "deploy", set, "--env", environment, "--catalog", catalog]);

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
