using jd.core.bp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.cli.tests;

// The CLI runs in-process against the seed catalog, the sample workload and the resolver's test environment.
public sealed class CliTests : IDisposable
{
    private static readonly string Workload = Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml");
    private static readonly string Environment = Path.Combine(AppContext.BaseDirectory, "golden", "seed-environment.yaml");
    private static readonly string Catalog = Path.Combine(AppContext.BaseDirectory, "catalog");
    private static readonly string Golden = Path.Combine(AppContext.BaseDirectory, "golden", "seed-workload.graph.json");

    private readonly string _temp = Directory.CreateTempSubdirectory("jd-cli-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private static Task<(int Code, string Out, string Err)> RunAsync(params string[] args) => RunWithAsync(null, args);

    private static async Task<(int Code, string Out, string Err)> RunWithAsync(IBackEndProvider? backend, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(args, stdout, stderr, CancellationToken.None, backend);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static async Task<(int Code, string Out, string Err)> RunWithEnvironmentAsync(Dictionary<string, string> variables, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(args, stdout, stderr, CancellationToken.None, null, name => variables.GetValueOrDefault(name));
        return (code, stdout.ToString(), stderr.ToString());
    }

    private sealed class RecordingBackend : IBackEndProvider
    {
        public List<string> Calls { get; } = [];
        public string? FailOn { get; set; }

        public Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken)
        {
            Calls.Add("deploy " + package.StackName);
            return package.StackName == FailOn
                ? throw new InvalidOperationException("provider exploded")
                : Task.FromResult(new DeploymentResult { Outputs = [], Summary = new() { ["Create"] = 1 }, Changes = [new ResourceChange { Urn = "urn", Type = "azure-native:t:T", Operation = "create" }] });
        }

        public Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken)
        {
            Calls.Add("preview " + package.StackName);
            return Task.FromResult(new DeploymentResult { Outputs = [], Summary = new() { ["Create"] = 1 } });
        }

        public Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string stackName, string deploymentContent, CancellationToken cancellationToken) =>
            Task.FromResult<Dictionary<string, ConfigEntry>?>(null);
    }

    private string WriteTemp(string name, string content)
    {
        var path = Path.Combine(_temp, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Validate_accepts_a_valid_workload()
    {
        var (code, stdout, _) = await RunAsync("validate", Workload);

        Assert.Equal(0, code);
        Assert.Contains("valid", stdout);
    }

    [Fact]
    public async Task Validate_prints_schema_errors_of_an_invalid_workload()
    {
        var path = WriteTemp("bad.yaml", "apiVersion: just-deliver/v1\nkind: Workload\n");

        var (code, _, stderr) = await RunAsync("validate", path);

        Assert.Equal(1, code);
        Assert.Contains(path, stderr);
        Assert.Contains("metadata", stderr);
    }

    [Fact]
    public async Task Validate_prints_workload_rule_errors()
    {
        var path = WriteTemp("dup.yaml", File.ReadAllText(Workload) + "  - type: cosmos-sql\n");

        var (code, _, stderr) = await RunAsync("validate", path);

        Assert.Equal(1, code);
        Assert.Contains($"{path}: requires[0], requires[1]: ", stderr);
        Assert.Contains("used more than once", stderr);
    }

    [Fact]
    public async Task A_nested_schema_error_is_one_line_with_file_and_location()
    {
        var path = WriteTemp("port.yaml", File.ReadAllText(Workload).Replace("port: 8080", "port: not-a-number"));

        var (code, _, stderr) = await RunAsync("validate", path);

        Assert.Equal(1, code);
        var line = Assert.Single(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith($"{path}: container.ports", line);
    }

    [Fact]
    public async Task Validate_reports_broken_yaml_with_the_file()
    {
        var path = WriteTemp("broken.yaml", "a: b\na: c\n");

        var (code, _, stderr) = await RunAsync("validate", path);

        Assert.Equal(1, code);
        Assert.Contains($"{path}: not valid YAML: duplicate key 'a'", stderr);
    }

    [Fact]
    public async Task Preview_lists_every_node_with_its_provenance()
    {
        var (code, stdout, stderr) = await RunAsync("preview", Workload, "--env", Environment, "--catalog", Catalog);

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        var nodes = JObject.Parse(File.ReadAllText(Golden))["nodes"]!.ToArray();
        Assert.NotEmpty(nodes);
        foreach (var node in nodes)
        {
            Assert.Contains((string)node["id"]!, stdout);
            Assert.Contains(((string)node["hash"]!)[..8], stdout);
        }

        // A policy-added field: the value, where it came from in the environment, and the policy that added it.
        Assert.Contains("  workspaceResourceId = /subscriptions/s/resourceGroups/rg-dev/providers/Microsoft.OperationalInsights/workspaces/law-dev (from env.logAnalytics.id)   [PolicyAdd: enforce-monitoring (policies/enforce-monitoring.yaml)]", stdout);
        // A mapping field: its provenance names the file once.
        Assert.Contains("  partitionKeyPath = /id   [Mapping: mappings/cosmos-sql/standard.yaml]", stdout);
        Assert.Contains("= pending: ", stdout);
        Assert.Contains("depends on: ", stdout);
    }

    [Fact]
    public async Task Preview_json_equals_the_golden_snapshot()
    {
        var (code, stdout, _) = await RunAsync("preview", Workload, "--env", Environment, "--catalog", Catalog, "--json");

        Assert.Equal(0, code);
        Assert.Equal(File.ReadAllText(Golden), stdout);
    }

    [Fact]
    public async Task Preview_of_an_invalid_workload_stops_before_resolution()
    {
        var path = WriteTemp("bad.yaml", "apiVersion: just-deliver/v1\nkind: Workload\n");
        var emptyCatalog = Directory.CreateDirectory(Path.Combine(_temp, "empty")).FullName;

        var (code, stdout, stderr) = await RunAsync("preview", path, "--env", Environment, "--catalog", emptyCatalog);

        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains(path, stderr);
        // An empty catalog is itself an error; seeing none proves the catalog was never loaded.
        Assert.DoesNotContain("kind Catalog", stderr);
    }

    [Fact]
    public async Task Preview_prints_resolver_errors_with_file_and_location()
    {
        var catalog = Path.Combine(_temp, "catalog");
        WriteTemp("catalog/catalog.yaml", "kind: Catalog\nversion: 1.0.0\n");
        WriteTemp("catalog/mappings/oops.yaml", "kind: Mapping\nmatch: {}\n");

        var (code, stdout, stderr) = await RunAsync("preview", Workload, "--env", Environment, "--catalog", catalog);

        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains(Path.Combine(catalog, "mappings/oops.yaml"), stderr);
    }

    [Fact]
    public async Task A_catalog_wide_error_is_not_given_a_file_path()
    {
        var emptyCatalog = Directory.CreateDirectory(Path.Combine(_temp, "empty")).FullName;

        var (code, _, stderr) = await RunAsync("preview", Workload, "--env", Environment, "--catalog", emptyCatalog);

        Assert.Equal(1, code);
        Assert.StartsWith("(catalog): ", stderr);
    }

    [Fact]
    public async Task Preview_prints_expansion_errors_with_the_workload_file()
    {
        var path = WriteTemp("unknown-type.yaml", File.ReadAllText(Workload).Replace("type: cosmos-sql", "type: no-such-type"));

        var (code, _, stderr) = await RunAsync("preview", path, "--env", Environment, "--catalog", Catalog);

        Assert.Equal(1, code);
        Assert.Contains(path, stderr);
        Assert.Contains("no-such-type", stderr);
    }

    [Fact]
    public async Task Deploy_deploys_infrastructure_nodes_and_reports_the_rest_as_waiting_for_the_runtime()
    {
        var backend = new RecordingBackend();

        var (code, stdout, stderr) = await RunWithAsync(backend, "deploy", Workload, "--env", Environment, "--catalog", Catalog);

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.Equal(["deploy just-deliver-sample-app.dev._workload.appinsights", "deploy just-deliver-sample-app.dev.cosmos-sql.container"], backend.Calls);
        Assert.Contains("just-deliver-sample-app/dev/cosmos-sql/container: deployed (create 1, ", stdout);
        Assert.Contains("  create azure-native:t:T", stdout);
        Assert.Contains("just-deliver-sample-app/dev/cosmos-sql/access: waiting for runtime (no changes, ", stdout);
    }

    [Fact]
    public async Task Deploy_with_preview_changes_nothing()
    {
        var backend = new RecordingBackend();

        var (code, stdout, _) = await RunWithAsync(backend, "deploy", Workload, "--env", Environment, "--catalog", Catalog, "--preview");

        Assert.Equal(0, code);
        Assert.All(backend.Calls, call => Assert.StartsWith("preview ", call));
        Assert.Contains("cosmos-sql/container: previewed (create 1, ", stdout);
    }

    [Fact]
    public async Task A_failed_deploy_exits_1_naming_the_node()
    {
        var backend = new RecordingBackend { FailOn = "just-deliver-sample-app.dev._workload.appinsights" };

        var (code, stdout, stderr) = await RunWithAsync(backend, "deploy", Workload, "--env", Environment, "--catalog", Catalog);

        Assert.Equal(1, code);
        Assert.Contains("appinsights: failed", stdout);
        Assert.Contains("jd: stopped at just-deliver-sample-app/dev/@workload/appinsights: provider exploded", stderr);
        Assert.Single(backend.Calls);
    }

    [Fact]
    public async Task Deploy_refuses_a_graph_that_does_not_fit_the_templates_before_creating_anything()
    {
        var catalog = Path.Combine(_temp, "catalog");
        CopyDirectory(Catalog, catalog);
        Directory.Delete(Path.Combine(catalog, "templates", "azure", "application-insights"), recursive: true);
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunWithAsync(backend, "deploy", Workload, "--env", Environment, "--catalog", catalog);

        Assert.Equal(1, code);
        Assert.Contains("template 'azure/application-insights' is not in the template library", stderr);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task Deploy_without_a_backend_url_exits_2_and_explains()
    {
        var (code, _, stderr) = await RunWithEnvironmentAsync(new() { ["PULUMI_CONFIG_PASSPHRASE"] = string.Empty }, "deploy", Workload, "--env", Environment, "--catalog", Catalog);

        Assert.Equal(2, code);
        Assert.Contains("jd: PULUMI_BACKEND_URL is not set", stderr);
        Assert.Contains("file://<directory>", stderr);
    }

    [Fact]
    public async Task Deploy_without_a_passphrase_exits_2_and_explains()
    {
        var (code, _, stderr) = await RunWithEnvironmentAsync(new() { ["PULUMI_BACKEND_URL"] = "file://x" }, "deploy", Workload, "--env", Environment, "--catalog", Catalog);

        Assert.Equal(2, code);
        Assert.Contains("jd: PULUMI_CONFIG_PASSPHRASE is not set", stderr);
        Assert.Contains("PULUMI_CONFIG_PASSPHRASE_FILE", stderr);
    }

    [Theory]
    [InlineData("PULUMI_CONFIG_PASSPHRASE", "")]
    [InlineData("PULUMI_CONFIG_PASSPHRASE_FILE", "/some/file")]
    public async Task An_empty_passphrase_or_a_passphrase_file_is_accepted(string name, string value)
    {
        // A catalog without the templates stops the run (exit 1) after the settings check and before any backend is created.
        var catalog = Path.Combine(_temp, "catalog");
        CopyDirectory(Catalog, catalog);
        Directory.Delete(Path.Combine(catalog, "templates"), recursive: true);

        var (code, _, stderr) = await RunWithEnvironmentAsync(new() { ["PULUMI_BACKEND_URL"] = "file://x", [name] = value }, "deploy", Workload, "--env", Environment, "--catalog", catalog);

        Assert.Equal(1, code);
        Assert.Contains("template directory not found", stderr);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("validate")]
    [InlineData("validate", "a.yaml", "--json")]
    [InlineData("preview", "a.yaml")]
    [InlineData("preview", "a.yaml", "--env")]
    [InlineData("preview", "a.yaml", "--bogus")]
    [InlineData("preview", "a.yaml", "--env", "e.yaml", "--catalog", "c", "--preview")]
    [InlineData("deploy", "a.yaml")]
    [InlineData("deploy", "a.yaml", "--env", "e.yaml", "--catalog", "c", "--json")]
    public async Task Usage_errors_exit_2_and_print_the_usage(params string[] args)
    {
        var (code, _, stderr) = await RunAsync(args);

        Assert.Equal(2, code);
        Assert.Contains("Usage:", stderr);
    }

    [Fact]
    public async Task No_arguments_is_a_usage_error()
    {
        Assert.Equal(2, (await RunAsync()).Code);
    }

    [Theory]
    [InlineData("validate", "missing.yaml")]
    [InlineData("preview", "missing.yaml", "--env", "env.yaml", "--catalog", "catalog")]
    public async Task A_missing_workload_file_exits_2(params string[] args)
    {
        var (code, _, stderr) = await RunAsync(args);

        Assert.Equal(2, code);
        Assert.Contains("missing.yaml", stderr);
    }

    [Fact]
    public async Task A_missing_environment_file_exits_2()
    {
        var (code, _, stderr) = await RunAsync("preview", Workload, "--env", "missing-env.yaml", "--catalog", Catalog);

        Assert.Equal(2, code);
        Assert.Contains("missing-env.yaml", stderr);
    }

    [Fact]
    public async Task A_directory_passed_as_the_workload_exits_2()
    {
        var (code, _, stderr) = await RunAsync("validate", _temp);

        Assert.Equal(2, code);
        Assert.Contains($"jd: cannot read '{_temp}': ", stderr);
    }

    [Fact]
    public async Task A_missing_catalog_directory_exits_2()
    {
        var missing = Path.Combine(_temp, "no-such-catalog");

        var (code, _, stderr) = await RunAsync("preview", Workload, "--env", Environment, "--catalog", missing);

        Assert.Equal(2, code);
        Assert.Contains($"jd: cannot read '{missing}': ", stderr);
    }

    [Fact]
    public async Task A_file_passed_as_the_catalog_exits_2()
    {
        var (code, _, stderr) = await RunAsync("preview", Workload, "--env", Environment, "--catalog", Environment);

        Assert.Equal(2, code);
        Assert.Contains($"jd: cannot read '{Environment}': ", stderr);
    }

    [Fact]
    public async Task A_directory_passed_as_the_environment_exits_2()
    {
        var (code, _, stderr) = await RunAsync("preview", Workload, "--env", _temp, "--catalog", Catalog);

        Assert.Equal(2, code);
        Assert.Contains($"jd: cannot read '{_temp}': ", stderr);
    }

    [Fact]
    public async Task A_file_the_process_cannot_read_exits_2_with_the_path()
    {
        var path = WriteTemp("locked.yaml", "kind: Workload\n");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        if (CanRead(path))
        {
            return; // Running as a user that ignores file permissions (root).
        }

        var (code, _, stderr) = await RunAsync("validate", path);

        Assert.Equal(2, code);
        Assert.StartsWith("jd: ", stderr);
        Assert.Contains(path, stderr);
    }

    private static bool CanRead(string path)
    {
        try
        {
            File.ReadAllBytes(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Help_prints_the_usage_and_exits_0()
    {
        var (code, stdout, _) = await RunAsync("--help");

        Assert.Equal(0, code);
        Assert.Contains("jd validate", stdout);
        Assert.Contains("jd preview", stdout);
        Assert.Contains("jd deploy", stdout);
    }
}
