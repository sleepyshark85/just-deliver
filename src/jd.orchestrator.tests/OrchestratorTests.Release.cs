using jd.core.bp;
using jd.resolver.catalog;
using jd.resolver.expansion;
using jd.resolver.expressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.orchestrator.tests;

// The runtime node's release flow: dark revision, grants, probe, traffic shift (docs/architecture/provisioning.md).
public partial class OrchestratorTests
{
    private const string RuntimeStack = "shop.dev._workload.runtime";
    private const string RuntimeId = "shop/dev/@workload/runtime";

    // A revision is live when the traffic the runtime stack last deployed names it.
    private static void SeedLive(FakeBackend backend, string traffic = """[{"revisionName":"app--old","weight":100}]""") =>
        backend.State[RuntimeStack] = new() { ["traffic"] = new(traffic) };

    private static async Task<RunReport> ReleaseAsync(FakeBackend backend, FakeProbe? probe = null)
    {
        var (graph, catalog) = await ResolveAsync();
        return await Create(backend, catalog, probe).DeployAsync(graph, null, CancellationToken.None);
    }

    private static NodeReport Runtime(RunReport report) => report.Nodes.Single(n => n.NodeId == RuntimeId);

    private static List<DeploymentPackage> RuntimeDeploys(FakeBackend backend) =>
        backend.Calls.Where(c => c.Op == "deploy" && c.Package.StackName == RuntimeStack).Select(c => c.Package).ToList();

    private static string Parameter(DeploymentPackage package, string name) => package.DeploymentParameters[name].Value ?? string.Empty;

    private static FakeProbe Failing(FakeBackend backend) => new(backend.Log) { Answer = _ => new ProbeResult(false, "HTTP 503") };

    [Fact]
    public async Task The_dark_deploy_never_sends_traffic_to_the_new_revision_when_a_live_revision_exists()
    {
        var backend = new FakeBackend();
        SeedLive(backend);

        var report = await ReleaseAsync(backend);

        Assert.True(report.Succeeded);
        var deploys = RuntimeDeploys(backend);
        var suffix = Parameter(deploys[0], "suffix");
        Assert.Equal("""[{"revisionName":"app--old","weight":100}]""", Parameter(deploys[0], "traffic"));
        Assert.DoesNotContain(suffix, Parameter(deploys[0], "traffic"));
        // The shift is the only deploy that names the new revision, with the same suffix, so it creates no revision.
        Assert.Equal(2, deploys.Count);
        Assert.Equal(suffix, Parameter(deploys[1], "suffix"));
        Assert.Equal($$"""[{"revisionName":"app--{{suffix}}","weight":100}]""", Parameter(deploys[1], "traffic"));
    }

    [Fact]
    public async Task A_failing_probe_is_retried_up_to_timeout_over_interval_and_the_revision_never_gets_traffic()
    {
        var backend = new FakeBackend();
        SeedLive(backend);
        var probe = Failing(backend);

        var report = await ReleaseAsync(backend, probe);

        var runtime = Runtime(report);
        Assert.False(report.Succeeded);
        Assert.Equal(NodeOutcome.Failed, runtime.Outcome);
        // probe: timeoutSeconds 60, intervalSeconds 5 -> 1 + 60 / 5 attempts, waiting the interval between them.
        Assert.Equal(13, probe.Calls.Count);
        Assert.Equal(12, probe.Delays.Count);
        Assert.All(probe.Delays, d => Assert.Equal(TimeSpan.FromSeconds(5), d));
        Assert.Equal("probe: HTTP 503 after 13 attempts", runtime.Message);
        Assert.Equal(
            [(Step.Dark, StepOutcome.Done, "app--" + Parameter(RuntimeDeploys(backend)[0], "suffix"), 0), (Step.Grants, StepOutcome.Done, string.Empty, 0), (Step.Probe, StepOutcome.Failed, "HTTP 503 after 13 attempts", 13)],
            runtime.Steps?.Select(s => (s.Step, s.Outcome, s.Detail, s.Attempts)));
        Assert.Single(RuntimeDeploys(backend)); // the dark deploy; no shift
        Assert.Equal(runtime, report.Nodes[^1]);
    }

    [Fact]
    public async Task A_probe_that_passes_after_some_failures_stops_retrying_and_the_revision_is_shifted()
    {
        var backend = new FakeBackend();
        SeedLive(backend);
        var probe = new FakeProbe(backend.Log) { Answer = attempt => new ProbeResult(attempt >= 3, "HTTP " + (attempt >= 3 ? "200" : "503")) };

        var report = await ReleaseAsync(backend, probe);

        Assert.True(report.Succeeded);
        Assert.Equal(3, probe.Calls.Count);
        Assert.Equal(2, probe.Delays.Count);
        Assert.Equal(2, RuntimeDeploys(backend).Count);
    }

    [Fact]
    public async Task The_probe_target_is_the_dark_deploys_fqdn_output_and_the_runtimes_path_only()
    {
        var backend = new FakeBackend { Fqdn = "rev-1.example.test" };
        var probe = new FakeProbe(backend.Log);

        await ReleaseAsync(backend, probe);

        var (fqdn, spec) = Assert.Single(probe.Calls);
        Assert.Equal("rev-1.example.test", fqdn);
        Assert.Equal(new Probe("/health", 200, 60, 5), spec);
    }

    [Fact]
    public async Task A_missing_fqdn_output_fails_the_probe_step_without_calling_the_probe()
    {
        var backend = new FakeBackend { Fqdn = null };
        SeedLive(backend);
        var probe = new FakeProbe(backend.Log);

        var report = await ReleaseAsync(backend, probe);

        var runtime = Runtime(report);
        Assert.Equal(NodeOutcome.Failed, runtime.Outcome);
        Assert.Equal("probe: the deploy exported no 'fqdn' to probe.", runtime.Message);
        Assert.Empty(probe.Calls);
        Assert.Single(RuntimeDeploys(backend));
    }

    [Fact]
    public async Task The_steps_run_in_order_dark_grants_probe_shift_and_the_runtime_is_reported_last()
    {
        var backend = new FakeBackend();
        SeedLive(backend);
        var reported = new List<string>();
        var (graph, catalog) = await ResolveAsync();

        var report = await Create(backend, catalog).DeployAsync(graph, r => reported.Add(r.NodeId), CancellationToken.None);

        // The runtime sorts first in graph order, yet comes after the infrastructure; the grant (rt) is deployed inside its Grants step.
        Assert.Equal(
            ["deploy shop.dev.thing.group", "deploy shop.dev.thing.app", "deploy shop.dev.thing.app2", $"deploy {RuntimeStack}", "deploy shop.dev.thing.rt", "probe", $"deploy {RuntimeStack}"],
            backend.Log);
        Assert.Equal(["shop/dev/thing/group", "shop/dev/thing/app", "shop/dev/thing/app2", "shop/dev/thing/rt", RuntimeId], reported);
        Assert.Equal(reported, report.Nodes.Select(n => n.NodeId));
        Assert.Equal([Step.Dark, Step.Grants, Step.Probe, Step.Shift], Runtime(report).Steps?.Select(s => s.Step));
        Assert.All(Runtime(report).Steps ?? [], s => Assert.Equal(StepOutcome.Done, s.Outcome));
        Assert.All(report.Nodes.Where(n => n.NodeId != RuntimeId), n => Assert.Null(n.Steps));
    }

    [Fact]
    public async Task A_failing_grant_stops_before_the_probe_and_the_shift()
    {
        var backend = new FakeBackend { FailOn = "shop.dev.thing.rt" };
        SeedLive(backend);
        var probe = new FakeProbe(backend.Log);

        var report = await ReleaseAsync(backend, probe);

        var runtime = Runtime(report);
        Assert.Equal(NodeOutcome.Failed, runtime.Outcome);
        Assert.Equal("grants: shop/dev/thing/rt: provider exploded", runtime.Message);
        Assert.Equal([Step.Dark, Step.Grants], runtime.Steps?.Select(s => s.Step));
        Assert.Equal(StepOutcome.Failed, runtime.Steps?[^1].Outcome);
        Assert.Equal(NodeOutcome.Failed, report.Nodes[^2].Outcome);
        Assert.Empty(probe.Calls);
        Assert.Single(RuntimeDeploys(backend));
    }

    [Fact]
    public async Task A_failing_dark_deploy_fails_the_dark_step_and_nothing_after_it_runs()
    {
        var backend = new FakeBackend { FailOn = RuntimeStack };

        var report = await ReleaseAsync(backend);

        var runtime = Runtime(report);
        Assert.Equal("dark: provider exploded", runtime.Message);
        Assert.Equal([(Step.Dark, StepOutcome.Failed, "provider exploded")], runtime.Steps?.Select(s => (s.Step, s.Outcome, s.Detail)));
        Assert.DoesNotContain(backend.Log, entry => entry.EndsWith(".rt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rerun_with_the_same_config_leaves_the_runtime_unchanged_without_probe_or_shift_but_still_deploys_the_grants()
    {
        var backend = new FakeBackend { DetectChanges = true };
        SeedLive(backend);
        var probe = new FakeProbe(backend.Log);
        var (graph, catalog) = await ResolveAsync();
        var orchestrator = Create(backend, catalog, probe);
        await orchestrator.DeployAsync(graph, null, CancellationToken.None); // dark, probe, shift
        var probesBefore = probe.Calls.Count;
        var deploysBefore = RuntimeDeploys(backend).Count;

        var report = await orchestrator.DeployAsync(graph, null, CancellationToken.None);

        var runtime = Runtime(report);
        Assert.Equal(NodeOutcome.Unchanged, runtime.Outcome);
        Assert.Equal([StepOutcome.Done, StepOutcome.Done, StepOutcome.Skipped, StepOutcome.Skipped], runtime.Steps?.Select(s => s.Outcome));
        Assert.Equal(probesBefore, probe.Calls.Count);
        Assert.Equal(deploysBefore + 1, RuntimeDeploys(backend).Count); // only the dark deploy
        Assert.Equal(2, backend.Calls.Count(c => c.Op == "deploy" && c.Package.StackName == "shop.dev.thing.rt"));
        Assert.Contains(report.Nodes, n => n.NodeId == "shop/dev/thing/rt");
    }

    [Fact]
    public async Task A_rerun_after_a_probe_failure_probes_and_shifts_the_same_revision_without_creating_another()
    {
        var backend = new FakeBackend { DetectChanges = true };
        SeedLive(backend);
        var probe = Failing(backend);
        var (graph, catalog) = await ResolveAsync();
        var orchestrator = Create(backend, catalog, probe);
        var failed = await orchestrator.DeployAsync(graph, null, CancellationToken.None);
        probe.Answer = _ => new ProbeResult(true, "HTTP 200");

        var report = await orchestrator.DeployAsync(graph, null, CancellationToken.None);

        Assert.False(failed.Succeeded);
        Assert.True(report.Succeeded);
        Assert.Equal(NodeOutcome.Deployed, Runtime(report).Outcome);
        var deploys = RuntimeDeploys(backend); // dark, dark again, shift
        Assert.Equal(3, deploys.Count);
        Assert.Single(deploys.Select(d => Parameter(d, "suffix")).Distinct());
        Assert.Equal(StepOutcome.Done, Runtime(report).Steps?[^1].Outcome);
        Assert.Equal($$"""[{"revisionName":"app--{{Parameter(deploys[0], "suffix")}}","weight":100}]""", Parameter(deploys[2], "traffic"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""[{"latestRevision":true,"weight":100}]""")]
    [InlineData("""[{"revisionName":"","weight":100}]""")]
    public async Task A_first_deploy_darks_on_the_latest_revision_probes_and_shifts_so_the_stack_names_the_revision(string? previousTraffic)
    {
        var backend = new FakeBackend();
        if (previousTraffic is not null)
        {
            SeedLive(backend, previousTraffic);
        }

        var report = await ReleaseAsync(backend);

        var runtime = Runtime(report);
        Assert.Equal(NodeOutcome.Deployed, runtime.Outcome);
        var deploys = RuntimeDeploys(backend);
        Assert.Equal("""[{"latestRevision":true,"weight":100}]""", Parameter(deploys[0], "traffic"));
        Assert.Contains("probe", backend.Log);
        Assert.All(runtime.Steps ?? [], s => Assert.Equal(StepOutcome.Done, s.Outcome));
        Assert.Equal(Step.Shift, runtime.Steps?[^1].Step);
        Assert.Equal(2, deploys.Count);
        var revision = $"app--{Parameter(deploys[0], "suffix")}";
        Assert.Equal($$"""[{"revisionName":"{{revision}}","weight":100}]""", Parameter(deploys[1], "traffic"));
        Assert.Equal(revision, runtime.Steps?[^1].Detail);
    }

    [Fact]
    public async Task The_runtime_report_carries_the_shifts_outputs_so_it_names_the_live_revision()
    {
        var backend = new FakeBackend();
        SeedLive(backend);

        var report = await ReleaseAsync(backend);

        var deploys = RuntimeDeploys(backend);
        Assert.Equal(Parameter(deploys[1], "traffic"), Runtime(report).Outputs["traffic"].Value);
        Assert.Contains(Parameter(deploys[0], "suffix"), Runtime(report).Outputs["traffic"].Value);
    }

    [Fact]
    public async Task A_first_deploy_that_fails_its_probe_is_failed_and_not_shifted()
    {
        var backend = new FakeBackend();

        var report = await ReleaseAsync(backend, Failing(backend)); // no prior revision exists: the latest one serves, and the run says it failed

        Assert.Equal(NodeOutcome.Failed, Runtime(report).Outcome);
        Assert.StartsWith("probe: HTTP 503", Runtime(report).Message, StringComparison.Ordinal);
        Assert.Single(RuntimeDeploys(backend));
    }

    [Fact]
    public async Task A_same_config_rerun_after_a_first_deploy_is_unchanged_without_probe_or_shift()
    {
        var backend = new FakeBackend { DetectChanges = true };
        var probe = new FakeProbe(backend.Log);
        var (graph, catalog) = await ResolveAsync();
        var orchestrator = Create(backend, catalog, probe);
        await orchestrator.DeployAsync(graph, null, CancellationToken.None);

        var report = await orchestrator.DeployAsync(graph, null, CancellationToken.None);

        Assert.Equal(NodeOutcome.Unchanged, Runtime(report).Outcome);
        Assert.Equal([StepOutcome.Done, StepOutcome.Done, StepOutcome.Skipped, StepOutcome.Skipped], Runtime(report).Steps?.Select(s => s.Outcome));
        Assert.Single(probe.Calls);
        Assert.Equal(3, RuntimeDeploys(backend).Count); // dark and shift of the first run, the dark of the second
    }

    [Fact]
    public async Task A_changed_config_after_a_first_deploy_darks_with_the_first_revision_live_and_shifts_only_after_the_probe()
    {
        var backend = new FakeBackend { DetectChanges = true };
        foreach (var value in new[] { "one", "two" })
        {
            var (graph, catalog) = await ResolveAsync(
                DefaultRequires, ExportingPart, runtime: RuntimeReadingNodes,
                container: $"container:\n  image: reg/app:1\n  ports:\n    - port: 8080\n  variables:\n    A: {value}\n");
            Assert.True((await Create(backend, catalog).DeployAsync(graph, null, CancellationToken.None)).Succeeded);
        }

        var deploys = RuntimeDeploys(backend); // dark and shift of the first release, then of the second
        Assert.Equal(4, deploys.Count);
        var (first, second) = ($"app--{Parameter(deploys[0], "suffix")}", $"app--{Parameter(deploys[2], "suffix")}");
        Assert.NotEqual(first, second);
        Assert.Equal($$"""[{"revisionName":"{{first}}","weight":100}]""", Parameter(deploys[2], "traffic"));
        Assert.Equal($$"""[{"revisionName":"{{second}}","weight":100}]""", Parameter(deploys[3], "traffic"));
        var log = backend.Log.Where(e => e == "probe" || e == $"deploy {RuntimeStack}").ToList();
        Assert.Equal(["deploy " + RuntimeStack, "probe", "deploy " + RuntimeStack, "deploy " + RuntimeStack, "probe", "deploy " + RuntimeStack], log);
    }

    [Fact]
    public async Task The_traffic_parameter_keeps_its_json_types_an_integer_weight_and_a_boolean_latest_flag()
    {
        var first = new FakeBackend();
        var rerun = new FakeBackend();
        SeedLive(rerun);

        await ReleaseAsync(first);
        await ReleaseAsync(rerun);

        var latest = JArray.Parse(Parameter(RuntimeDeploys(first)[0], "traffic")).Single();
        Assert.Equal(JTokenType.Boolean, latest["latestRevision"]?.Type);
        Assert.Equal(JTokenType.Integer, latest["weight"]?.Type);
        foreach (var package in RuntimeDeploys(rerun))
        {
            var item = JArray.Parse(Parameter(package, "traffic")).Single();
            Assert.Equal(JTokenType.String, item["revisionName"]?.Type);
            Assert.Equal(JTokenType.Integer, item["weight"]?.Type);
        }
    }

    [Fact]
    public async Task A_release_stops_after_a_workloads_probe_failure_and_the_later_workloads_are_untouched()
    {
        var backend = new FakeBackend();
        var probe = Failing(backend);
        var (alpha, catalog) = await ResolveAsync(workloadName: "alpha");
        var (beta, _) = await ResolveAsync(workloadName: "beta");

        var runs = await Create(backend, catalog, probe).DeployReleaseAsync([alpha, beta], preview: false, null, null, CancellationToken.None);

        var run = Assert.Single(runs);
        Assert.False(run.Run.Succeeded);
        Assert.Equal("probe: HTTP 503 after 13 attempts", run.Run.Nodes[^1].Message);
        Assert.DoesNotContain(backend.Calls, c => c.Package.StackName.StartsWith("beta.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preview_deploys_nothing_never_calls_the_probe_and_plans_the_steps()
    {
        var backend = new FakeBackend();
        SeedLive(backend);
        var probe = new FakeProbe(backend.Log);
        var (graph, catalog) = await ResolveAsync();

        var report = await Create(backend, catalog, probe).PreviewAsync(graph, null, CancellationToken.None);

        Assert.DoesNotContain(backend.Calls, c => c.Op == "deploy");
        Assert.Empty(probe.Calls);
        var runtime = Runtime(report);
        Assert.Equal(NodeOutcome.Previewed, runtime.Outcome);
        Assert.Equal([Step.Dark, Step.Grants, Step.Probe, Step.Shift], runtime.Steps?.Select(s => s.Step));
        Assert.All(runtime.Steps ?? [], s => Assert.Equal(StepOutcome.Planned, s.Outcome));
        Assert.Equal(RuntimeId, report.Nodes[^1].NodeId);
        // The previewed package is the dark one: the live revision keeps the traffic.
        var package = backend.Calls.Single(c => c.Op == "preview" && c.Package.StackName == RuntimeStack).Package;
        Assert.Equal("""[{"revisionName":"app--old","weight":100}]""", Parameter(package, "traffic"));
    }

    // The suffix of the runtime's dark deploy for a workload with this image and these variables.
    private static async Task<string> SuffixAsync(string image, string variables)
    {
        var backend = new FakeBackend();
        var (graph, catalog) = await ResolveAsync(
            DefaultRequires, ExportingPart, runtime: RuntimeReadingNodes,
            container: $"container:\n  image: {image}\n  ports:\n    - port: 8080\n  variables:\n{variables}");
        await Create(backend, catalog).DeployAsync(graph, null, CancellationToken.None);
        return Parameter(RuntimeDeploys(backend)[0], "suffix");
    }

    [Fact]
    public async Task The_suffix_follows_the_config_it_changes_with_the_image_and_with_a_variable_and_is_valid_for_azure()
    {
        var baseline = await SuffixAsync("reg/app:1", "    A: one\n");

        Assert.Equal(baseline, await SuffixAsync("reg/app:1", "    A: one\n"));
        Assert.NotEqual(baseline, await SuffixAsync("reg/app:2", "    A: one\n"));
        Assert.NotEqual(baseline, await SuffixAsync("reg/app:1", "    A: two\n"));
        Assert.NotEqual(baseline, await SuffixAsync("reg/app:1", "    B: one\n"));
        // Azure: lowercase letters, digits and '-', starting with a letter, ending with a letter or digit; the revision name <app>--<suffix> at most 54 characters.
        Assert.Matches("^[a-z][a-z0-9-]*[a-z0-9]$", baseline);
        Assert.Matches("^r[0-9a-f]{14}$", baseline);
        Assert.Equal(Hash(string.Empty)[..6], baseline[^6..]); // a first deploy has no previous suffix: the salt is that of the empty string
    }

    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static async Task<RunReport> DeployVariantAsync(FakeBackend backend, string value)
    {
        var (graph, catalog) = await ResolveAsync(
            DefaultRequires, ExportingPart, runtime: RuntimeReadingNodes,
            container: $"container:\n  image: reg/app:1\n  ports:\n    - port: 8080\n  variables:\n    A: {value}\n");
        return await Create(backend, catalog).DeployAsync(graph, null, CancellationToken.None);
    }

    [Fact]
    public async Task Going_back_to_an_earlier_config_deploys_a_new_revision_that_is_probed_and_shifted()
    {
        var backend = new FakeBackend { DetectChanges = true };

        await DeployVariantAsync(backend, "one");
        await DeployVariantAsync(backend, "two");
        var revert = await DeployVariantAsync(backend, "one");

        var deploys = RuntimeDeploys(backend); // dark and shift of each release
        Assert.Equal(6, deploys.Count);
        var suffixes = new[] { 0, 2, 4 }.Select(i => Parameter(deploys[i], "suffix")).ToList();
        Assert.Equal(3, suffixes.Distinct().Count());
        Assert.Equal(suffixes[0][..9], suffixes[2][..9]); // the same config part, a different salt
        Assert.NotEqual(suffixes[0][9..], suffixes[2][9..]);
        Assert.Equal(NodeOutcome.Deployed, Runtime(revert).Outcome);
        Assert.Equal(3, backend.Log.Count(e => e == "probe"));
        Assert.Equal($$"""[{"revisionName":"app--{{suffixes[1]}}","weight":100}]""", Parameter(deploys[4], "traffic")); // dark: the second is live
        Assert.Equal($$"""[{"revisionName":"app--{{suffixes[2]}}","weight":100}]""", Parameter(deploys[5], "traffic"));
    }

    [Fact]
    public async Task A_changed_template_with_the_same_parameters_gets_a_new_suffix()
    {
        var backend = new FakeBackend { DetectChanges = true };
        var (graph, catalog) = await ResolveAsync();
        var orchestrator = Create(backend, catalog);
        await orchestrator.DeployAsync(graph, null, CancellationToken.None);
        backend.ContentVersion = "-v2";

        var report = await orchestrator.DeployAsync(graph, null, CancellationToken.None);

        var deploys = RuntimeDeploys(backend);
        Assert.NotEqual(Parameter(deploys[0], "suffix"), Parameter(deploys[2], "suffix"));
        Assert.Equal(NodeOutcome.Deployed, Runtime(report).Outcome);
    }

    [Fact]
    public async Task The_suffix_ignores_suffix_and_traffic_entries_in_the_runtime_config_and_the_engine_sets_both()
    {
        var plain = new FakeBackend();
        var tampered = new FakeBackend();
        var (graph, catalog) = await ResolveAsync();
        var withEntries = graph with
        {
            Nodes = graph.Nodes.Select(n => n.Name == "runtime"
                ? n with
                {
                    Config = new Dictionary<string, ConfigValue>(n.Config)
                    {
                        ["suffix"] = new ConfigText(new Resolved("zzz"), new HashSet<string>()),
                        ["traffic"] = new ConfigText(new Resolved("[]"), new HashSet<string>()),
                    },
                }
                : n).ToList(),
        };

        await Create(plain, catalog).DeployAsync(graph, null, CancellationToken.None);
        await Create(tampered, catalog).DeployAsync(withEntries, null, CancellationToken.None);

        var dark = RuntimeDeploys(tampered)[0];
        Assert.Equal(Parameter(RuntimeDeploys(plain)[0], "suffix"), Parameter(dark, "suffix"));
        Assert.Equal("""[{"latestRevision":true,"weight":100}]""", Parameter(dark, "traffic"));
    }
}
