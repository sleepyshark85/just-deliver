using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using jd.bp.pulumi;
using jd.core.bp;
using jd.resolver.environment;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace jd.cli.tests;

/// <summary>
/// Real resources, free tier only: the sample <c>shared</c> and <c>dev</c> definitions (renamed with a random suffix, so names cannot
/// collide) brought up with <c>jd env up</c> on a local file state. Checks the descriptors, the free-tier Cosmos account and its
/// RU/s cap and the workspace's daily cap through <c>az</c>, and that running each again changes nothing. The <c>stage</c> definition
/// (type <c>stage</c>, with its approval policy) is brought up on the same account: its own database of 400 RU/s, the account limit still
/// 1000, a descriptor that carries the policy, and a re-run that changes nothing. Then the sample release set is
/// deployed on <c>dev</c> (<c>jd release create</c>, <c>jd release deploy</c>): the workloads get distinct containers in the dev database
/// without throughput of their own, so the free-tier RU/s budget holds, and a second deploy changes nothing. The live apps hold no key (the account
/// refuses key auth, the apps have no secret), run on Consumption scaled to zero, write and read an item, and their telemetry reaches App Insights. Then a release whose first
/// workload's <c>/health</c> fails (its container does not exist) stops at the probe with traffic still on the good revision and the
/// later workload untouched, and the good release deployed again goes live as a new revision. Then <c>jd release qualify</c> passes on the live
/// good release; <c>jd approve</c> refuses an unknown label and, under the strict <c>stage</c> policy, the principal that deployed the set, then records an approval
/// under a relaxed policy and refuses a repeat; <c>jd promote</c> refuses the set before the approval (nothing is written) and, after it, deploys the kept set to <c>stage</c>
/// (same bytes, a deploy record with IQ passed, the same image digests). Last, the worker's image is changed out of band: <c>qualify</c> fails naming the image check and <c>approve</c> and <c>promote</c> are refused as not qualified. It is one scenario because
/// Azure allows one free-tier Cosmos account per subscription and the substrate here is that account. Always deletes the resource
/// groups it created. Run with <c>tools/verify.sh --azure</c>; needs the sandbox team identity (<c>ARM_CLIENT_ID</c>, <c>ARM_CLIENT_SECRET</c>,
/// <c>ARM_TENANT_ID</c>, <c>ARM_SUBSCRIPTION_ID</c>) in the environment, <c>JD_REGION</c> and the pulumi CLI, and a subscription
/// <b>without an existing free-tier Cosmos account</b> (Azure allows one per subscription; the test creates it and deletes it).
/// </summary>
[Trait("Category", "Azure")]
public sealed class EnvUpAzureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jd-azure-" + Guid.NewGuid().ToString("N"));
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly AzureCli _az = AzureCli.Login();

    private string SharedName => "s12sh" + _suffix;

    private string DevName => "s12dv" + _suffix;

    private string StageName => "s12st" + _suffix;

    public void Dispose()
    {
        try
        {
            DeleteResourceGroups();
        }
        finally
        {
            _az.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    // The groups are named after their environment, so the suffix finds them even when a deploy failed before writing a descriptor.
    // A failed teardown fails the test: a resource group left behind must never go unnoticed. The environment groups are deleted in
    // parallel (each takes about 27 minutes), the shared group last: the Container Apps environment of every other group logs to its workspace.
    private List<string> TestGroups()
    {
        var list = _az.Run("group", "list", "--subscription", _az.Subscription, "--query", $"[?tags.project=='just-deliver-mvp' && contains(name, '{_suffix}')].name", "-o", "tsv");
        return list.Code == 0
            ? list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).OrderBy(g => g.Contains(SharedName, StringComparison.Ordinal) ? 1 : 0).ToList()
            : throw new InvalidOperationException($"Could not list the test resource groups; run tools/azure/cleanup.sh --yes --all. {list.Error}");
    }

    private void DeleteResourceGroups()
    {
        var groups = TestGroups();
        var environments = groups.Where(g => !g.Contains(SharedName, StringComparison.Ordinal)).ToList();
        foreach (var group in environments)
        {
            Delete(group, "--no-wait");
        }

        foreach (var group in environments)
        {
            var wait = _az.Run("group", "wait", "--deleted", "--name", group, "--subscription", _az.Subscription, "--timeout", "3600");
            if (wait.Code != 0)
            {
                throw new InvalidOperationException($"Resource group {group} was not deleted; run tools/azure/cleanup.sh --yes --all. {wait.Error}");
            }
        }

        foreach (var group in groups.Except(environments))
        {
            Delete(group);
        }

        var left = TestGroups();
        if (left.Count > 0)
        {
            throw new InvalidOperationException($"Resource groups left behind: {string.Join(", ", left)}; run tools/azure/cleanup.sh --yes --all.");
        }
    }

    private void Delete(string group, params string[] extra)
    {
        var delete = _az.Run(["group", "delete", "--name", group, "--subscription", _az.Subscription, "--yes", .. extra]);
        if (delete.Code != 0)
        {
            throw new InvalidOperationException($"Could not delete resource group {group}; run tools/azure/cleanup.sh --yes --all. {delete.Error}");
        }
    }

    private string Definition(string sample, string name)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "environments", sample + ".yaml"));
        var path = Path.Combine(_root, sample + ".yaml");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, text.Replace($"\nname: {sample}\n", $"\nname: {name}\n", StringComparison.Ordinal));
        return path;
    }

    // The value of a --query, as tsv.
    private string Az(params string[] arguments)
    {
        var result = _az.Run([.. arguments, "--subscription", _az.Subscription, "-o", "tsv"]);
        return result.Code == 0 ? result.Output.Trim() : throw new InvalidOperationException($"az {string.Join(' ', arguments)} failed: {result.Error}");
    }

    private static async Task<(int Code, string Output)> UpAsync(IBackEndProvider backend, string region, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(["env", "up", .. args, "--catalog", Path.Combine(AppContext.BaseDirectory, "catalog"), "--region", region, "--force"], stdout, stderr, CancellationToken.None, backend);
        return (code, stdout + stderr.ToString());
    }

    // Every node line reports no changes: the second run found everything as the first left it (a runtime whose live revision is
    // the one just deployed is unchanged too, with no probe or shift).
    private static void AssertNothingChanged(string output, int nodes)
    {
        Assert.Equal(nodes, output.Split('\n').Count(line => line.Contains(": unchanged (no changes", StringComparison.Ordinal)));
        Assert.DoesNotContain(": deployed", output);
    }

    // The directory of the release records (JD_RECORDS_DIR); the identity of a release deploy is the ARM_* principal of the process.
    private string Records => Path.Combine(_root, "records");

    private async Task<(int Code, string Output)> ReleaseAsync(IBackEndProvider backend, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Directory.CreateDirectory(Records);
        var code = await Cli.RunAsync(
            ["release", .. args], stdout, stderr, CancellationToken.None, backend, name => name == "JD_RECORDS_DIR" ? Records : System.Environment.GetEnvironmentVariable(name));
        return (code, stdout + stderr.ToString());
    }

    // The deploy records of a label on dev (or the environment given), oldest first.
    private List<JsonElement> RecordsOf(string label, string? environment = null) =>
        Directory.GetFiles(Path.Combine(Records, environment ?? DevName, label), "*-deploy.json").Order(StringComparer.Ordinal).Select(file =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.Clone();
        }).ToList();

    // The qualify records of a label on dev, oldest first.
    private List<JsonElement> QualifyRecordsOf(string label) =>
        Directory.GetFiles(Path.Combine(Records, DevName, label), "*-qualify.json").Order(StringComparer.Ordinal).Select(file =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.Clone();
        }).ToList();

    private static string Iq(JsonElement record) => record.GetProperty("iq").GetProperty("status").GetString() ?? string.Empty;

    private static string Oq(JsonElement record, int workload) => record.GetProperty("workloads")[workload].GetProperty("oq").GetProperty("status").GetString() ?? string.Empty;

    // The stage descriptor with its policy pointed at the renamed dev and at the principal that ran the deploys (the sandbox has one), strict or relaxed about separation.
    private string StagePolicy(string stageDescriptor, string file, string principal, bool separate)
    {
        var text = ReplaceRequired(File.ReadAllText(stageDescriptor), @"qualifiedOn:\s*dev\b", $"qualifiedOn: {DevName}");
        text = ReplaceRequired(text, "00000000-0000-0000-0000-000000000000", principal);
        var path = Path.Combine(_root, file);
        File.WriteAllText(path, ReplaceRequired(text, @"separateFromDeployer:\s*true", $"separateFromDeployer: {(separate ? "true" : "false")}"));
        return path;
    }

    // A rewrite of a descriptor that must change it: a pattern that no longer matches fails the test instead of leaving the text as it was.
    private static string ReplaceRequired(string text, string pattern, string replacement)
    {
        Assert.Matches(pattern, text);
        return Regex.Replace(text, pattern, replacement.Replace("$", "$$", StringComparison.Ordinal));
    }

    private async Task<(int Code, string Output)> ApproveAsync(string label, string stageDescriptor)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(
            ["approve", label, "--env", stageDescriptor, "--meaning", "Azure test: reviewed the evidence."], stdout, stderr, CancellationToken.None,
            environmentVariable: name => name == "JD_RECORDS_DIR" ? Records : System.Environment.GetEnvironmentVariable(name));
        return (code, stdout + stderr.ToString());
    }

    // The promotion of a label to the gated environment of the descriptor, with the real backend; the output is stdout and stderr together.
    private async Task<(int Code, string Output)> PromoteAsync(IBackEndProvider backend, string label, string stageDescriptor)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(
            ["promote", label, "--to", stageDescriptor, "--catalog", Path.Combine(AppContext.BaseDirectory, "catalog")], stdout, stderr, CancellationToken.None, backend,
            name => name == "JD_RECORDS_DIR" ? Records : System.Environment.GetEnvironmentVariable(name));
        return (code, stdout + stderr.ToString());
    }

    // The value of a JMESPath query on a resource (its Azure resource id carries the subscription), as tsv.
    private string Show(string resourceId, string query)
    {
        var result = _az.Run("resource", "show", "--ids", resourceId, "--query", query, "-o", "tsv");
        return result.Code == 0 ? result.Output.Trim() : throw new InvalidOperationException($"az resource show {resourceId} failed: {result.Error}");
    }

    // The value of an environment variable of the Container App's container (its Azure resource id).
    private string AppEnv(string appId, string name) => Show(appId, $"properties.template.containers[0].env[?name=='{name}'].value | [0]");

    // The app scales to zero and gives each Cosmos call 5 s, so the first requests after a cold start may answer 503 while its client starts:
    // a request is sent again on 503, 12 times at most, 10 s apart (two minutes).
    private static async Task<HttpResponseMessage> ServedAsync(Func<Task<HttpResponseMessage>> send)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await send();
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable || attempt == 12)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }

    // The number of requests the App Insights component holds from the last day. Ingestion takes minutes, so a count of zero is asked
    // again, 20 times at most, 30 s apart (ten minutes); the last count is returned.
    private async Task<long> RequestTelemetryAsync(string componentId)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = _az.Run("monitor", "app-insights", "query", "--apps", componentId, "--analytics-query", "requests | count", "--offset", "1d", "--query", "tables[0].rows[0][0]", "-o", "tsv");
            var count = result.Code == 0 ? long.Parse(result.Output.Trim(), CultureInfo.InvariantCulture) : throw new InvalidOperationException($"az monitor app-insights query on {componentId} failed: {result.Error}");
            if (count > 0 || attempt == 20)
            {
                return count;
            }

            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    // The Container App in the group whose COSMOS_CONTAINER starts with the prefix: the app names are not known to the test.
    private string AppId(string group, string containerPrefix) =>
        Az("resource", "list", "--resource-group", group, "--resource-type", "Microsoft.App/containerApps", "--query", "[].id")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(id => AppEnv(id, "COSMOS_CONTAINER").StartsWith(containerPrefix, StringComparison.Ordinal));

    // The revisions of the app by name with their traffic weight (a revision without traffic reports none).
    private Dictionary<string, int> Revisions(string appId)
    {
        var result = _az.Run("rest", "--method", "get", "--url", $"{appId}/revisions?api-version=2024-03-01", "--query", "value[].{name:name,weight:properties.trafficWeight}", "-o", "json");
        return result.Code == 0
            ? System.Text.Json.JsonDocument.Parse(result.Output).RootElement.EnumerateArray().ToDictionary(
                revision => revision.GetProperty("name").GetString()!,
                revision => revision.GetProperty("weight") is { ValueKind: System.Text.Json.JsonValueKind.Number } weight ? weight.GetInt32() : 0)
            : throw new InvalidOperationException($"az rest revisions of {appId} failed: {result.Error}");
    }

    // The sample manifest and workloads in a new folder under the given label, with the sample worker's container variable naming a
    // container that does not exist: the worker's /health answers 503. The worker is first in the set; the app depends on it.
    private async Task<string> BadReleaseSetAsync(IBackEndProvider backend, string label)
    {
        var folder = Path.Combine(_root, "bad-" + label);
        Directory.CreateDirectory(folder);
        var samples = Path.Combine(AppContext.BaseDirectory, "samples");
        foreach (var workload in new[] { "sample-app", "sample-worker" })
        {
            var text = File.ReadAllText(Path.Combine(samples, "workloads", workload + ".yaml"));
            var bad = workload == "sample-worker" ? text.Replace("COSMOS_CONTAINER: ${resource.database.container}", "COSMOS_CONTAINER: no-such-container", StringComparison.Ordinal) : text;
            Assert.Equal(workload == "sample-worker", bad != text);
            File.WriteAllText(Path.Combine(folder, workload + ".yaml"), bad);
        }

        File.WriteAllText(
            Path.Combine(folder, "manifest.yaml"),
            File.ReadAllText(Path.Combine(samples, "releases", "sample.manifest.yaml")).Replace("../workloads/", string.Empty, StringComparison.Ordinal).Replace("label: 1.0.0", $"label: {label}", StringComparison.Ordinal));
        var set = Path.Combine(folder, "release.yaml");
        var (code, output) = await ReleaseAsync(backend, "create", Path.Combine(folder, "manifest.yaml"), "--out", set);
        Assert.True(code == 0, output);
        return set;
    }

    [Fact]
    public async Task Shared_then_dev_are_provisioned_and_a_release_set_deploys_onto_dev_within_the_free_tier_and_reruns_change_nothing()
    {
        var region = System.Environment.GetEnvironmentVariable("JD_REGION")
            ?? throw new InvalidOperationException("The Azure tests need JD_REGION (see docs/plans/status.md, Environment).");
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterPulumiBackend(options =>
        {
            options.BackendUrl = "file://" + Path.Combine(_root, "state");
            options.ConfigPassPhrase = "test-passphrase";
            options.ScratchDirectory = Path.Combine(_root, "scratch");
        });
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        var backend = services.BuildServiceProvider().GetRequiredService<IBackEndProvider>();
        var shared = Definition("shared", SharedName);
        var dev = Definition("dev", DevName);
        var sharedDescriptor = Path.Combine(_root, "shared.env.yaml");
        var devDescriptor = Path.Combine(_root, "dev.env.yaml");

        var (sharedCode, sharedOutput) = await UpAsync(backend, region, shared, "--out", sharedDescriptor);
        Assert.True(sharedCode == 0, sharedOutput);
        var loadedShared = (await EnvironmentFile.LoadAsync(sharedDescriptor)).Descriptor;
        Assert.NotNull(loadedShared);
        var account = loadedShared.Values["cosmos.accountName"];
        var sharedGroup = loadedShared.Values["shared.resourceGroup"];
        Assert.Equal("true", Az("cosmosdb", "show", "--name", account, "--resource-group", sharedGroup, "--query", "enableFreeTier"), ignoreCase: true);
        Assert.Equal("1000", Az("cosmosdb", "show", "--name", account, "--resource-group", sharedGroup, "--query", "capacity.totalThroughputLimit"));
        var cap = Az("monitor", "log-analytics", "workspace", "show", "--workspace-name", loadedShared.Values["logAnalytics.name"], "--resource-group", sharedGroup, "--query", "workspaceCapping.dailyQuotaGb");
        Assert.Equal(0.15, double.Parse(cap, System.Globalization.CultureInfo.InvariantCulture));

        var (devCode, devOutput) = await UpAsync(backend, region, dev, "--base", sharedDescriptor, "--out", devDescriptor);
        Assert.True(devCode == 0, devOutput);
        var loadedDev = (await EnvironmentFile.LoadAsync(devDescriptor)).Descriptor;
        Assert.NotNull(loadedDev);
        Assert.Equal(account, loadedDev.Values["cosmos.accountName"]);
        Assert.Equal(["cosmos.accountId"], loadedDev.Grantable);
        var database = loadedDev.Values["cosmos.databaseName"];
        Assert.Equal("400", Az("cosmosdb", "sql", "database", "throughput", "show", "--account-name", account, "--resource-group", sharedGroup, "--name", database, "--query", "resource.throughput"));

        var (againSharedCode, againShared) = await UpAsync(backend, region, shared, "--out", sharedDescriptor);
        Assert.True(againSharedCode == 0, againShared);
        AssertNothingChanged(againShared, 3);
        var (againDevCode, againDev) = await UpAsync(backend, region, dev, "--base", sharedDescriptor, "--out", devDescriptor);
        Assert.True(againDevCode == 0, againDev);
        AssertNothingChanged(againDev, 3);

        // stage: dev's layout with an approval policy, on the same account. Its own database holds 400 RU/s, so the two use 800 of the free 1,000
        // and the account limit stays; the descriptor says protected and carries the policy; running it again changes nothing.
        var stage = Definition("stage", StageName);
        var stageDescriptor = Path.Combine(_root, "stage.env.yaml");
        var (stageCode, stageOutput) = await UpAsync(backend, region, stage, "--base", sharedDescriptor, "--out", stageDescriptor);
        Assert.True(stageCode == 0, stageOutput);
        var loadedStage = (await EnvironmentFile.LoadAsync(stageDescriptor)).Descriptor;
        Assert.NotNull(loadedStage);
        Assert.Equal(("stage", "dev", 1, true), (loadedStage.Type, loadedStage.Approval?.QualifiedOn, loadedStage.Approval?.Required, loadedStage.Approval?.SeparateFromDeployer));
        Assert.Equal(["00000000-0000-0000-0000-000000000000"], loadedStage.Approval!.Approvers);
        Assert.Equal(account, loadedStage.Values["cosmos.accountName"]);
        var stageDatabase = loadedStage.Values["cosmos.databaseName"];
        Assert.NotEqual(database, stageDatabase);
        Assert.Equal("400", Az("cosmosdb", "sql", "database", "throughput", "show", "--account-name", account, "--resource-group", sharedGroup, "--name", stageDatabase, "--query", "resource.throughput"));
        Assert.Equal("400", Az("cosmosdb", "sql", "database", "throughput", "show", "--account-name", account, "--resource-group", sharedGroup, "--name", database, "--query", "resource.throughput"));
        Assert.Equal("1000", Az("cosmosdb", "show", "--name", account, "--resource-group", sharedGroup, "--query", "capacity.totalThroughputLimit"));
        var (againStageCode, againStage) = await UpAsync(backend, region, stage, "--base", sharedDescriptor, "--out", stageDescriptor);
        Assert.True(againStageCode == 0, againStage);
        AssertNothingChanged(againStage, 3);

        // The sample release set on dev: two workloads, one container each in the dev database, each runtime a real Container App released through dark revision, grants and probe.
        var releaseSet = Path.Combine(_root, "release.yaml");
        var (createCode, createOutput) = await ReleaseAsync(backend, "create", Path.Combine(AppContext.BaseDirectory, "samples", "releases", "sample.manifest.yaml"), "--out", releaseSet);
        Assert.True(createCode == 0, createOutput);
        var deploy = new[] { "deploy", releaseSet, "--env", devDescriptor, "--catalog", Path.Combine(AppContext.BaseDirectory, "catalog") };
        var (releaseCode, releaseOutput) = await ReleaseAsync(backend, deploy);
        Assert.True(releaseCode == 0, releaseOutput);
        // Each workload's runtime is deployed through the release flow, a real Container App: the grants (a database role assignment and a
        // metrics-publisher assignment) are deployed in its Grants step, the real HTTP probe finds the new revision healthy, and the shift
        // names it (also on a first deploy), so the next run finds it live.
        var releaseLines = releaseOutput.Split('\n');
        foreach (var grant in new[] { "/database/access", "/@workload/appinsights-access" })
        {
            Assert.Equal(2, releaseLines.Count(line => line.Contains($"{grant}: deployed", StringComparison.Ordinal)));
        }

        Assert.Equal(2, releaseLines.Count(line => line.Contains("/@workload/runtime: deployed", StringComparison.Ordinal)));
        Assert.Equal(2, releaseLines.Count(line => line.Trim() == "probe: done (HTTP 200)"));
        Assert.Equal(2, releaseLines.Count(line => line.Trim().StartsWith("shift: done", StringComparison.Ordinal)));
        // In the set's order: the worker, which the app depends on, first.
        var workerStarted = Array.FindIndex(releaseLines, line => line.Trim() == "workload just-deliver-sample-worker");
        Assert.True(workerStarted >= 0 && Array.FindIndex(releaseLines, line => line.Trim() == "workload just-deliver-sample-app") > workerStarted, releaseOutput);

        // The deploy left a record naming the principal the run authenticated with (as Entra reports it) and a passed OQ for each workload.
        var firstRecord = Assert.Single(RecordsOf("1.0.0"));
        Assert.Equal("succeeded", firstRecord.GetProperty("outcome").GetString());
        Assert.Equal(System.Environment.GetEnvironmentVariable("ARM_CLIENT_ID"), firstRecord.GetProperty("identity").GetProperty("clientId").GetString());
        Assert.Equal(System.Environment.GetEnvironmentVariable("ARM_TENANT_ID"), firstRecord.GetProperty("identity").GetProperty("tenantId").GetString());
        Assert.Equal(["passed", "passed"], [Oq(firstRecord, 0), Oq(firstRecord, 1)]);
        Assert.Equal("passed", Iq(firstRecord)); // the deploy qualified itself: no drift, the pinned digest live
        // The set's second workload changed nothing of the first: IQ, after both were deployed, refreshed each of the worker's five nodes against Azure and found no drift.
        var workerDrift = firstRecord.GetProperty("iq").GetProperty("checks").EnumerateArray()
            .Where(check => check.GetProperty("workload").GetString() == "just-deliver-sample-worker" && check.GetProperty("kind").GetString() == "drift").ToList();
        Assert.Equal(5, workerDrift.Count);
        Assert.All(workerDrift, check => Assert.Equal("passed", check.GetProperty("status").GetString()));
        // The deploy time is measured and recorded: every node and every workload's run took time, the walk's own time holds the runs (each rounded to
        // 3 decimals), and IQ is timed apart.
        var runs = firstRecord.GetProperty("workloads").EnumerateArray().Select(workload => workload.GetProperty("run")).ToList();
        Assert.All(runs.SelectMany(run => run.GetProperty("nodes").EnumerateArray()), node => Assert.True(node.GetProperty("elapsedSeconds").GetDouble() > 0));
        var runSeconds = runs.Select(run => run.GetProperty("elapsedSeconds").GetDouble()).ToList();
        Assert.All(runSeconds, seconds => Assert.True(seconds > 0));
        Assert.True(firstRecord.GetProperty("deploySeconds").GetDouble() >= runSeconds.Sum() - 0.002, $"deploySeconds {firstRecord.GetProperty("deploySeconds")} is less than the runs' {runSeconds.Sum()}");
        Assert.True(firstRecord.GetProperty("iq").GetProperty("elapsedSeconds").GetDouble() > 0);

        var containers = Az("cosmosdb", "sql", "container", "list", "--account-name", account, "--resource-group", sharedGroup, "--database-name", database, "--query", "[].name")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, containers.Length);
        Assert.Equal(2, containers.Distinct().Count());
        Assert.Single(containers, name => name.StartsWith("just-deliver-sample-app-", StringComparison.Ordinal));
        Assert.Single(containers, name => name.StartsWith("just-deliver-sample-worker-", StringComparison.Ordinal));

        // Free-tier RU/s budget: a container with shared throughput has none of its own (Azure answers the throughput query with an error),
        // the database keeps its 400 and the account still refuses anything beyond 1,000.
        foreach (var container in containers)
        {
            var own = _az.Run("cosmosdb", "sql", "container", "throughput", "show", "--account-name", account, "--resource-group", sharedGroup, "--database-name", database, "--name", container, "--subscription", _az.Subscription);
            Assert.NotEqual(0, own.Code);
        }

        Assert.Equal("400", Az("cosmosdb", "sql", "database", "throughput", "show", "--account-name", account, "--resource-group", sharedGroup, "--name", database, "--query", "resource.throughput"));
        Assert.Equal("1000", Az("cosmosdb", "show", "--name", account, "--resource-group", sharedGroup, "--query", "capacity.totalThroughputLimit"));

        var (againReleaseCode, againRelease) = await ReleaseAsync(backend, deploy);
        Assert.True(againReleaseCode == 0, againRelease);
        AssertNothingChanged(againRelease, 10); // five nodes per workload: the runtime and four others

        // The second pass on the deployed runtime: the pending variables hold real values; the secret one reaches the app and never the output.
        var devGroup = loadedDev.Values["resourceGroup"];
        var workerApp = AppId(devGroup, "just-deliver-sample-worker-");
        var sampleApp = AppId(devGroup, "just-deliver-sample-app-");
        Assert.StartsWith("https://", AppEnv(sampleApp, "COSMOS_ENDPOINT"), StringComparison.Ordinal);
        Assert.Contains(AppEnv(sampleApp, "COSMOS_CONTAINER"), containers);
        Assert.StartsWith("InstrumentationKey=", AppEnv(sampleApp, "APPLICATIONINSIGHTS_CONNECTION_STRING"), StringComparison.Ordinal);
        Assert.DoesNotContain("InstrumentationKey", releaseOutput + againRelease, StringComparison.Ordinal);

        // No key or connection string for the data: the account refuses key authentication, and each app holds no secret and no variable backed by
        // one; its variables are the workload's addresses and the App Insights connection string. Free tier: the environment has no profile other
        // than Consumption, and each app scales to zero at 0.25 vCPU and 0.5Gi.
        Assert.Equal("true", Az("cosmosdb", "show", "--name", account, "--resource-group", sharedGroup, "--query", "disableLocalAuth"), ignoreCase: true);
        Assert.Equal("0", Show(loadedDev.Values["containerAppsEnvironment.id"], "length(properties.workloadProfiles[?workloadProfileType!='Consumption'] || `[]`)"));
        foreach (var app in new[] { workerApp, sampleApp })
        {
            Assert.Equal("0", Show(app, "length(properties.configuration.secrets || `[]`)"));
            Assert.Equal("0", Show(app, "length(properties.template.containers[0].env[?secretRef])"));
            Assert.Equal(
                ["APPLICATIONINSIGHTS_CONNECTION_STRING", "COSMOS_CONTAINER", "COSMOS_DATABASE", "COSMOS_ENDPOINT", "LOG_LEVEL"],
                Show(app, "properties.template.containers[0].env[].name").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal));
            Assert.Equal(("0", "0.25", "0.5Gi"), (Show(app, "properties.template.scale.minReplicas"), Show(app, "properties.template.containers[0].resources.cpu"), Show(app, "properties.template.containers[0].resources.memory")));
        }

        // The app reads and writes its container with its identity: an item written through the live revision is read back.
        using (var http = new HttpClient { BaseAddress = new Uri("https://" + Show(sampleApp, "properties.configuration.ingress.fqdn")), Timeout = TimeSpan.FromMinutes(1) })
        {
            using var created = await ServedAsync(() => http.PostAsJsonAsync("/items", new { text = "s20-" + _suffix }));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var read = await ServedAsync(() => http.GetAsync(created.Headers.Location));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Contains("s20-" + _suffix, await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A worker whose /health fails never gets traffic: the probe fails with the real timeout, the good revision keeps 100%, and the app,
        // later in the set, is not started.
        var goodWorker = Revisions(workerApp);
        var goodApp = Revisions(sampleApp);
        var good = Assert.Single(goodWorker);
        Assert.Equal(100, good.Value);
        // A different set under the label of the good one is refused before anything is deployed (set.yaml keeps the first one).
        var (refusedCode, refusedOutput) = await ReleaseAsync(backend, "deploy", await BadReleaseSetAsync(backend, "1.0.0"), "--env", devDescriptor, "--catalog", Path.Combine(AppContext.BaseDirectory, "catalog"));
        Assert.NotEqual(0, refusedCode);
        Assert.Contains("holds a different release set", refusedOutput, StringComparison.Ordinal);
        Assert.Equal(2, RecordsOf("1.0.0").Count);
        var (badCode, badOutput) = await ReleaseAsync(backend, "deploy", await BadReleaseSetAsync(backend, "1.0.1-bad"), "--env", devDescriptor, "--catalog", Path.Combine(AppContext.BaseDirectory, "catalog"));
        var badLines = badOutput.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).ToList();
        Assert.NotEqual(0, badCode);
        Assert.Contains(badLines, line => line.StartsWith("probe: failed", StringComparison.Ordinal));
        Assert.DoesNotContain(badLines, line => line.StartsWith("shift:", StringComparison.Ordinal));
        Assert.DoesNotContain("workload just-deliver-sample-app", badLines);
        var afterBad = Revisions(workerApp);
        Assert.Equal(2, afterBad.Count);
        Assert.Equal(100, afterBad[good.Key]);
        Assert.Equal(0, afterBad.Single(revision => revision.Key != good.Key).Value);
        Assert.Equal(goodApp, Revisions(sampleApp));
        var badRecord = Assert.Single(RecordsOf("1.0.1-bad"));
        Assert.Equal("failed", badRecord.GetProperty("outcome").GetString());
        Assert.Equal(["failed", "notRun"], [Oq(badRecord, 0), Oq(badRecord, 1)]);
        Assert.Equal(["just-deliver-sample-app"], badRecord.GetProperty("notStarted").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal("notRun", Iq(badRecord)); // a failed deploy runs no IQ

        // Going back to the good release deploys the old config as a new revision, probed and given all the traffic; the app is unchanged.
        var (revertCode, revertOutput) = await ReleaseAsync(backend, deploy);
        Assert.True(revertCode == 0, revertOutput);
        var reverted = Revisions(workerApp);
        Assert.Equal(3, reverted.Count);
        var live = Assert.Single(reverted, revision => revision.Value == 100).Key;
        Assert.DoesNotContain(live, afterBad.Keys);
        Assert.Equal(goodApp, Revisions(sampleApp));

        // Telemetry reaches App Insights: the component whose connection string the app holds has its requests (the probe of the first deploy and the
        // item calls, minutes ago by now, so the bounded wait rarely waits).
        var connection = AppEnv(sampleApp, "APPLICATIONINSIGHTS_CONNECTION_STRING");
        var component = Az("resource", "list", "--resource-group", devGroup, "--resource-type", "Microsoft.Insights/components", "--query", "[].id")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(id => Show(id, "properties.ConnectionString") == connection);
        Assert.True(await RequestTelemetryAsync(component) > 0, $"no request telemetry reached {component} within ten minutes");

        // The good release's records: the first deploy, the re-run that changed nothing and the revert, each its own file.
        var records = RecordsOf("1.0.0");
        Assert.Equal(3, records.Count);
        Assert.All(records, record => Assert.Equal("succeeded", record.GetProperty("outcome").GetString()));
        Assert.Equal(["unchanged", "unchanged"], [Oq(records[1], 0), Oq(records[1], 1)]);
        Assert.Equal("passed", Oq(records[2], 0));
        Assert.Equal("passed", Iq(records[2]));

        // IQ: the good release, as deployed, qualifies (no drift anywhere, the pinned digest on every revision that serves traffic).
        var catalog = Path.Combine(AppContext.BaseDirectory, "catalog");
        var qualify = new[] { "qualify", releaseSet, "--env", devDescriptor, "--catalog", catalog };
        var (qualifiedCode, qualifiedOutput) = await ReleaseAsync(backend, qualify);
        Assert.True(qualifiedCode == 0, qualifiedOutput);
        var qualified = Assert.Single(QualifyRecordsOf("1.0.0"));
        Assert.Equal("qualify", qualified.GetProperty("activity").GetString());
        Assert.Equal("passed", qualified.GetProperty("iq").GetProperty("status").GetString());
        Assert.Equal(System.Environment.GetEnvironmentVariable("ARM_CLIENT_ID"), qualified.GetProperty("identity").GetProperty("clientId").GetString());

        // Approval: the stage policy names the renamed dev and the principal that ran the deploys. An unknown label is refused; under the strict policy the
        // principal is refused as the deployer of the set; under the relaxed copy it is approved, the record names the deployer's own identity and the hash of dev's
        // set.yaml, and a repeat is refused. (Separation between different principals is proved offline: the sandbox has one.)
        var principal = RecordsOf("1.0.0")[0].GetProperty("identity").GetProperty("objectId").GetString()!;
        var strictStage = StagePolicy(stageDescriptor, "strict.stage.env.yaml", principal, separate: true);
        var relaxedStage = StagePolicy(stageDescriptor, "relaxed.stage.env.yaml", principal, separate: false);
        var (unknownCode, unknownOutput) = await ApproveAsync("9.9.9", relaxedStage);
        Assert.Equal(1, unknownCode);
        Assert.Contains("unknown label", unknownOutput, StringComparison.Ordinal);
        var (strictCode, strictOutput) = await ApproveAsync("1.0.0", strictStage);
        Assert.Equal(1, strictCode);
        Assert.Contains($"principal {principal} deployed 1.0.0 on {DevName}", strictOutput, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Records, StageName)), "a refusal wrote a record");
        var (unapprovedCode, unapprovedOutput) = await PromoteAsync(backend, "1.0.0", relaxedStage); // qualified, but nobody approved it
        Assert.Equal(1, unapprovedCode);
        Assert.Contains($"0 of 1 required approvals for '{StageName}' count.", unapprovedOutput, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Records, StageName)), "a refused promotion wrote a record");
        var (approvedCode, approvedOutput) = await ApproveAsync("1.0.0", relaxedStage);
        Assert.True(approvedCode == 0, approvedOutput);
        Assert.Contains($"approved 1.0.0 for {StageName} (1 of 1)", approvedOutput, StringComparison.Ordinal);
        using var approval = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(Records, StageName, "1.0.0"), "*-approve.json"))));
        Assert.Equal(principal, approval.RootElement.GetProperty("identity").GetProperty("objectId").GetString());
        Assert.Equal(System.Environment.GetEnvironmentVariable("ARM_CLIENT_ID"), approval.RootElement.GetProperty("identity").GetProperty("clientId").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Records, DevName, "1.0.0", "set.yaml")))), approval.RootElement.GetProperty("release").GetProperty("setSha256").GetString());
        var (repeatCode, repeatOutput) = await ApproveAsync("1.0.0", relaxedStage);
        Assert.Equal(1, repeatCode);
        Assert.Contains("already approved", repeatOutput, StringComparison.Ordinal);

        // Promotion: the approved set goes to stage by the same walk, from the bytes kept on dev. Stage keeps those bytes, its deploy record carries IQ (passed) and the
        // hash of the set, and both environments run the same image digests.
        var (promotedCode, promotedOutput) = await PromoteAsync(backend, "1.0.0", relaxedStage);
        Assert.True(promotedCode == 0, promotedOutput);
        var devSet = Path.Combine(Records, DevName, "1.0.0", "set.yaml");
        Assert.Equal(File.ReadAllBytes(devSet), File.ReadAllBytes(Path.Combine(Records, StageName, "1.0.0", "set.yaml")));
        var promoted = Assert.Single(RecordsOf("1.0.0", StageName));
        Assert.Equal("succeeded", promoted.GetProperty("outcome").GetString());
        Assert.Equal("passed", Iq(promoted));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(devSet))), promoted.GetProperty("release").GetProperty("setSha256").GetString());
        Func<JsonElement, string[]> images = record => [.. record.GetProperty("workloads").EnumerateArray().Select(workload => workload.GetProperty("image").GetString()!)];
        Assert.Equal(images(RecordsOf("1.0.0")[^1]), images(promoted));

        // The image of the worker's app is changed out of band, and that revision is given all the traffic. The quickstart image is a small public
        // Microsoft image that needs no registry login; it listens on port 80, so the app's ingress is pointed there first, for the new revision to start serving.
        const string OtherImage = "mcr.microsoft.com/k8se/quickstart:latest";
        var workerName = _az.Run("resource", "show", "--ids", workerApp, "--query", "name", "-o", "tsv").Output.Trim();
        Assert.NotEmpty(workerName);
        var oob = $"{workerName}--oob1";
        foreach (var command in new[]
        {
            new[] { "containerapp", "ingress", "update", "--ids", workerApp, "--target-port", "80" },
            ["containerapp", "update", "--ids", workerApp, "--image", OtherImage, "--revision-suffix", "oob1"],
            ["containerapp", "ingress", "traffic", "set", "--ids", workerApp, "--revision-weight", $"{oob}=100"],
        })
        {
            var changed = _az.Run(command); // the app's id carries its subscription
            Assert.True(changed.Code == 0, $"az {string.Join(' ', command)} failed: {changed.Error}");
        }

        Assert.Equal(100, Revisions(workerApp)[oob]);
        var (drifted, driftedOutput) = await ReleaseAsync(backend, qualify);
        Assert.Equal(1, drifted);
        // The image check names what was pinned (a digest) and what runs, on which revision; this is the line that proves IQ read Azure.
        Assert.Matches($@"IQ failed: just-deliver-sample-worker: pinned \S+@sha256:[0-9a-f]{{64}}, live {Regex.Escape(OtherImage)} on revision {Regex.Escape(oob)}", driftedOutput);
        // The runtime node differs from its stack too: Azure's refresh reflects the change into the inputs (seen in the S17c1 gate).
        Assert.True(driftedOutput.Contains($"just-deliver-sample-worker/{DevName}/@workload/runtime differs from what is deployed", StringComparison.Ordinal), driftedOutput);
        Assert.DoesNotContain("just-deliver-sample-app: pinned", driftedOutput, StringComparison.Ordinal);
        var failedIq = QualifyRecordsOf("1.0.0");
        Assert.Equal(2, failedIq.Count);
        Assert.Equal("failed", failedIq[1].GetProperty("iq").GetProperty("status").GetString());

        // Approval follows the current evidence: with the image out of band the newest record says IQ failed, so the set is no longer qualified and nothing more is written.
        var (staleCode, staleOutput) = await ApproveAsync("1.0.0", relaxedStage);
        Assert.Equal(1, staleCode);
        Assert.Contains($"release 1.0.0 is not qualified on {DevName}", staleOutput, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(Records, StageName, "1.0.0"), "*-approve.json"));

        // The same holds for promotion: the newest evidence on dev says IQ failed, so the set cannot be promoted again and stage keeps its one deploy record.
        var (barredCode, barredOutput) = await PromoteAsync(backend, "1.0.0", relaxedStage);
        Assert.Equal(1, barredCode);
        Assert.Contains($"not qualified on {DevName}", barredOutput, StringComparison.Ordinal);
        Assert.Single(RecordsOf("1.0.0", StageName));
    }
}
