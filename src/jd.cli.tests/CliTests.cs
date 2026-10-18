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

    private static async Task<(int Code, string Out, string Err)> RunWithAsync(RecordingBackend? backend, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(args, stdout, stderr, CancellationToken.None, backend, probe: backend);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static async Task<(int Code, string Out, string Err)> RunWithEnvironmentAsync(Dictionary<string, string> variables, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(args, stdout, stderr, CancellationToken.None, null, name => variables.GetValueOrDefault(name));
        return (code, stdout.ToString(), stderr.ToString());
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
        var path = WriteTemp("dup.yaml", File.ReadAllText(Workload) + "  - type: database\n");

        var (code, _, stderr) = await RunAsync("validate", path);

        Assert.Equal(1, code);
        Assert.Contains($"{path}: requires[0], requires[2]: ", stderr);
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

        // A component field read from the environment: the value, where it came from, and the component file.
        Assert.Contains("  workspaceResourceId = /subscriptions/s/resourceGroups/rg-dev/providers/Microsoft.OperationalInsights/workspaces/law-dev (from env.logAnalytics.id)   [Implementation: components/monitoring.yaml]", stdout);
        // A mapping field: its provenance names the file once.
        Assert.Contains("  partitionKeyPath = /id   [Implementation: components/database.yaml]", stdout);
        Assert.Contains("= pending: ", stdout);
        Assert.Contains("depends on: ", stdout);
        // The runtime node: its own phase, the probe as data, its fields from the runtime mapping.
        Assert.Contains("template: azure/container-app  kind: Create  phase: Runtime", stdout);
        Assert.Contains("  probe: GET /health expects 200", stdout);
        Assert.Contains("  targetPort = 8080   [Implementation: runtimes/container-app.yaml]", stdout);
        // The env wiring: each variable with its source, and whether it still waits for a deployed output.
        Assert.Contains("  variables.LOG_LEVEL = info   [Workload: container.variables]", stdout);
        Assert.Contains("  variables.COSMOS_ENDPOINT = https://cosmos-dev.documents.azure.com:443/   [Workload: container.variables]", stdout);
        Assert.Contains("  variables.COSMOS_CONTAINER = pending: ${resource.database.container}   [Workload: container.variables]", stdout);
        // The connection string is the workload's own variable, read from its monitoring requirement; no policy sets it.
        Assert.Contains("  variables.APPLICATIONINSIGHTS_CONNECTION_STRING = pending: ${resource.monitoring.connectionString}   [Workload: container.variables]", stdout);
    }

    // Success criterion 5: a policy that exists only as a catalog file changes what preview shows, with the policy named as the provenance.
    [Fact]
    public async Task A_policy_added_as_a_catalog_file_only_shows_in_the_preview()
    {
        var catalog = Path.Combine(_temp, "catalog");
        CopyDirectory(Catalog, catalog);
        WriteTemp("catalog/policies/limit-revisions.yaml", "kind: Policy\nname: limit-revisions\nreason: Keep fewer inactive revisions\nmatch: { template: azure/container-app }\nset: { maxInactiveRevisions: 2 }\n");

        var (code, stdout, stderr) = await RunAsync("preview", Workload, "--env", Environment, "--catalog", catalog);

        Assert.True(code == 0, stderr);
        Assert.Contains("  maxInactiveRevisions = 2   [PolicySet: limit-revisions (policies/limit-revisions.yaml)]", stdout);
        Assert.DoesNotContain("  maxInactiveRevisions = 5   [Implementation: runtimes/container-app.yaml]", stdout);
    }

    // The manual-test kit (samples/walkthrough, docs/walkthrough.md) cannot rot unseen: every workload previews, every manifest makes a set whose
    // deploy preview (set check, resolution of every workload, template check, the walk) passes with the app before the worker, `release show` names the app as the worker's dependsOn (the order alone
    // could be an alphabetical tie-break), and the scenario-5 policy, copied into a catalog copy, shows in the preview.
    [Fact]
    public async Task The_walkthrough_kit_passes_the_offline_checks_and_its_policy_shows_in_the_preview()
    {
        var kit = Path.Combine(AppContext.BaseDirectory, "samples", "walkthrough");
        var workloads = Directory.GetFiles(Path.Combine(kit, "workloads"), "*.yaml");
        var manifests = Directory.GetFiles(Path.Combine(kit, "releases"), "*.manifest.yaml");
        Assert.NotEmpty(workloads);
        Assert.NotEmpty(manifests);
        foreach (var workload in workloads)
        {
            var (code, _, stderr) = await RunAsync("preview", workload, "--env", Environment, "--catalog", Catalog);
            Assert.True(code == 0, $"{workload}: {stderr}");
        }

        foreach (var manifest in manifests)
        {
            var set = Path.Combine(_temp, Path.GetFileName(manifest) + ".set.yaml");
            var (created, _, createError) = await RunAsync("release", "create", manifest, "--out", set);
            Assert.True(created == 0, $"{manifest}: {createError}");
            var (code, stdout, stderr) = await RunWithAsync(new RecordingBackend(), "release", "deploy", set, "--env", Environment, "--catalog", Catalog, "--preview");
            Assert.True(code == 0, $"{manifest}: {stderr}");
            Assert.Matches(@"release \S+ on dev: 2 previewed, 0 failed, 0 not started\.", stdout);
            var app = stdout.IndexOf("workload just-deliver-sample-app", StringComparison.Ordinal);
            Assert.True(app >= 0 && app < stdout.IndexOf("workload just-deliver-sample-worker", StringComparison.Ordinal), stdout);
            var (shown, show, showError) = await RunAsync("release", "show", set);
            Assert.True(shown == 0, showError);
            Assert.Contains("    depends on: just-deliver-sample-app", show);
        }

        var catalog = Path.Combine(_temp, "catalog");
        CopyDirectory(Catalog, catalog);
        File.Copy(Path.Combine(kit, "policies", "limit-revisions.yaml"), Path.Combine(catalog, "policies", "limit-revisions.yaml"));
        var (previewed, preview, previewError) = await RunAsync("preview", Path.Combine(kit, "workloads", "app.yaml"), "--env", Environment, "--catalog", catalog);
        Assert.True(previewed == 0, previewError);
        Assert.Contains("  maxInactiveRevisions = 2   [PolicySet: limit-revisions (policies/limit-revisions.yaml)]", preview);
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
    public async Task Preview_reports_a_component_export_that_references_a_resource_instead_of_failing()
    {
        var catalog = Path.Combine(_temp, "catalog");
        WriteTemp("catalog/catalog.yaml", "kind: Catalog\nversion: 1.0.0\nenvironmentTypes: { dev: {}, stage: { approval: true } }\n");
        WriteTemp("catalog/components/database.yaml", "kind: Component\nname: database\ndescription: d\nexports: [endpoint]\nimplementations:\n  default:\n    classes:\n      standard:\n        nodes:\n          n: { template: t/n, config: {} }\n        exports:\n          endpoint: ${resource.other.endpoint}\n");

        var (code, stdout, stderr) = await RunAsync("preview", Workload, "--env", Environment, "--catalog", catalog);

        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains(Path.Combine(catalog, "components/database.yaml"), stderr);
        Assert.Contains("implementations.default.classes.standard.exports.endpoint", stderr);
        Assert.Contains("an export cannot reference", stderr);
    }

    [Fact]
    public async Task Preview_prints_resolver_errors_with_file_and_location()
    {
        var catalog = Path.Combine(_temp, "catalog");
        WriteTemp("catalog/catalog.yaml", "kind: Catalog\nversion: 1.0.0\nenvironmentTypes: { dev: {}, stage: { approval: true } }\n");
        WriteTemp("catalog/components/oops.yaml", "kind: Component\nname: oops\n");

        var (code, stdout, stderr) = await RunAsync("preview", Workload, "--env", Environment, "--catalog", catalog);

        Assert.Equal(1, code);
        Assert.Empty(stdout);
        Assert.Contains(Path.Combine(catalog, "components/oops.yaml"), stderr);
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
        var path = WriteTemp("unknown-type.yaml", File.ReadAllText(Workload).Replace("type: database", "type: no-such-type"));

        var (code, _, stderr) = await RunAsync("preview", path, "--env", Environment, "--catalog", Catalog);

        Assert.Equal(1, code);
        Assert.Contains(path, stderr);
        Assert.Contains("no-such-type", stderr);
    }

    [Fact]
    public async Task Deploy_deploys_the_infrastructure_then_the_runtime_in_steps_with_its_grants_inside_the_grants_step()
    {
        var backend = new RecordingBackend();

        var (code, stdout, stderr) = await RunWithAsync(backend, "deploy", Workload, "--env", Environment, "--catalog", Catalog);

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        var app = "just-deliver-sample-app";
        Assert.Equal([$"deploy {app}.dev.database.container", $"deploy {app}.dev.monitoring.appinsights", $"deploy {app}.dev._workload.runtime"], backend.Calls.Take(3));
        Assert.Equal([$"deploy {app}.dev.database.access", $"deploy {app}.dev.monitoring.access"], backend.Calls.Skip(3).Take(2).Order(StringComparer.Ordinal));
        Assert.Equal("probe out-latestRevisionFqdn/health", backend.Calls[5]);
        Assert.Equal($"deploy {app}.dev._workload.runtime", backend.Calls[6]); // the shift, also on a first deploy
        Assert.Equal(7, backend.Calls.Count);
        Assert.Contains($"{app}/dev/database/container: deployed (create 1, ", stdout);
        Assert.Contains($"{app}/dev/database/access: deployed (create 1, ", stdout);
        // The runtime is reported last, one line per step.
        var lines = stdout.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var runtime = Array.FindIndex(lines, l => l.StartsWith($"{app}/dev/@workload/runtime: deployed (create 1, ", StringComparison.Ordinal));
        Assert.Equal(
            ["  create azure-native:t:T", "  dark: done (out-latestRevisionName)", "  grants: done", "  probe: done (HTTP 200)", "  shift: done (out-latestRevisionName)"],
            lines[(runtime + 1)..].ToList());
    }

    // The policy only requires monitoring (docs/architecture/components.md section 3): the runtime gets the connection string through the
    // workload's own mapping, and a secret output stays secret through the export into the variables entry.
    [Fact]
    public async Task The_connection_string_reaches_the_runtime_through_the_workloads_variable_and_stays_secret()
    {
        var backend = new RecordingBackend { SecretOutputs = { "connectionString" } };

        var (code, _, stderr) = await RunWithAsync(backend, "deploy", Workload, "--env", Environment, "--catalog", Catalog);

        Assert.True(code == 0, stderr);
        var variables = backend.Packages.Last(p => p.StackName == "just-deliver-sample-app.dev._workload.runtime").DeploymentParameters["variables"];
        Assert.True(variables.IsSecret);
        Assert.Contains("""{"name":"APPLICATIONINSIGHTS_CONNECTION_STRING","value":"out-connectionString"}""", variables.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workload_that_requires_monitoring_but_does_not_map_it_gets_no_connection_string()
    {
        var path = WriteTemp("unmapped.yaml", File.ReadAllText(Workload).Replace("    APPLICATIONINSIGHTS_CONNECTION_STRING: ${resource.monitoring.connectionString}\n", string.Empty, StringComparison.Ordinal));
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunWithAsync(backend, "deploy", path, "--env", Environment, "--catalog", Catalog);

        Assert.True(code == 0, stderr);
        Assert.Contains("deploy just-deliver-sample-app.dev.monitoring.appinsights", backend.Calls);
        var variables = backend.Packages.Last(p => p.StackName == "just-deliver-sample-app.dev._workload.runtime").DeploymentParameters["variables"];
        Assert.DoesNotContain("APPLICATIONINSIGHTS_CONNECTION_STRING", variables.Value, StringComparison.Ordinal);
        Assert.False(variables.IsSecret);
    }

    [Fact]
    public async Task A_workload_without_monitoring_is_refused_naming_the_policy_and_its_file()
    {
        var path = WriteTemp("unmonitored.yaml", File.ReadAllText(Workload)
            .Replace("    APPLICATIONINSIGHTS_CONNECTION_STRING: ${resource.monitoring.connectionString}\n", string.Empty, StringComparison.Ordinal)
            .Replace("  - type: monitoring\n", string.Empty, StringComparison.Ordinal));
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunWithAsync(backend, "deploy", path, "--env", Environment, "--catalog", Catalog);

        Assert.Equal(1, code);
        Assert.Contains($"{path}: requires: policy 'enforce-monitoring' (policies/enforce-monitoring.yaml) requires a 'monitoring' requirement (Every workload reports to the environment workspace); add '- type: monitoring'.", stderr);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task Deploy_with_preview_changes_nothing()
    {
        var backend = new RecordingBackend();

        var (code, stdout, _) = await RunWithAsync(backend, "deploy", Workload, "--env", Environment, "--catalog", Catalog, "--preview");

        Assert.Equal(0, code);
        Assert.All(backend.Calls, call => Assert.StartsWith("preview ", call));
        Assert.Contains("database/container: previewed (create 1, ", stdout);
        Assert.Contains("@workload/runtime: pending upstream", stdout); // it reads outputs of nodes a preview does not deploy
    }

    [Fact]
    public async Task A_revision_that_fails_its_probe_exits_1_naming_the_probe_step_and_is_never_shifted()
    {
        // One second of retrying instead of the runtime's five minutes.
        var catalog = Path.Combine(_temp, "catalog");
        CopyDirectory(Catalog, catalog);
        var runtime = Path.Combine(catalog, "runtimes", "container-app.yaml");
        File.WriteAllText(runtime, File.ReadAllText(runtime).Replace("timeoutSeconds: 300, intervalSeconds: 5", "timeoutSeconds: 1, intervalSeconds: 1", StringComparison.Ordinal));
        var backend = new RecordingBackend { ProbePasses = false };

        var (code, stdout, stderr) = await RunWithAsync(backend, "deploy", Workload, "--env", Environment, "--catalog", catalog);

        Assert.Equal(1, code);
        Assert.Contains("jd: stopped at just-deliver-sample-app/dev/@workload/runtime: probe: HTTP 503 after 2 attempts", stderr);
        Assert.Contains("  probe: failed (HTTP 503 after 2 attempts)", stdout);
        Assert.Single(stdout.Split("HTTP 503 after 2 attempts")[1..]); // the reason is printed once on stdout, not again as the node's message
        Assert.Single(backend.Calls, call => call == "deploy just-deliver-sample-app.dev._workload.runtime"); // the dark deploy only, no shift
        Assert.Equal(2, backend.Calls.Count(call => call.StartsWith("probe ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_failed_deploy_exits_1_naming_the_node()
    {
        var backend = new RecordingBackend { FailOn = "just-deliver-sample-app.dev.database.container" };

        var (code, stdout, stderr) = await RunWithAsync(backend, "deploy", Workload, "--env", Environment, "--catalog", Catalog);

        Assert.Equal(1, code);
        Assert.Contains("database/container: failed", stdout);
        Assert.Contains("jd: stopped at just-deliver-sample-app/dev/database/container: provider exploded", stderr);
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

    internal static void CopyDirectory(string from, string to)
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
