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
/// RU/s cap and the workspace's daily cap through <c>az</c>, and that running each again changes nothing. Then the sample release set is
/// deployed on <c>dev</c> (<c>jd release create</c>, <c>jd release deploy</c>): the workloads get distinct containers in the dev database
/// without throughput of their own, so the free-tier RU/s budget holds, and a second deploy changes nothing. Then a release whose first
/// workload's <c>/health</c> fails (its container does not exist) stops at the probe with traffic still on the good revision and the
/// later workload untouched, and the good release deployed again goes live as a new revision. Last, <c>jd release qualify</c> passes on the live
/// good release, then fails after the worker's image is changed out of band, naming the image check. It is one scenario because
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
    // A failed teardown fails the test: a resource group left behind must never go unnoticed. The dev group goes first: its
    // Container Apps environment logs to the shared workspace.
    private List<string> TestGroups()
    {
        var list = _az.Run("group", "list", "--subscription", _az.Subscription, "--query", $"[?tags.project=='just-deliver-mvp' && contains(name, '{_suffix}')].name", "-o", "tsv");
        return list.Code == 0
            ? list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).OrderBy(g => g.Contains(DevName, StringComparison.Ordinal) ? 0 : 1).ToList()
            : throw new InvalidOperationException($"Could not list the test resource groups; run tools/azure/cleanup.sh --yes --all. {list.Error}");
    }

    private void DeleteResourceGroups()
    {
        foreach (var group in TestGroups())
        {
            var delete = _az.Run("group", "delete", "--name", group, "--subscription", _az.Subscription, "--yes");
            if (delete.Code != 0)
            {
                throw new InvalidOperationException($"Could not delete resource group {group}; run tools/azure/cleanup.sh --yes --all. {delete.Error}");
            }
        }

        var left = TestGroups();
        if (left.Count > 0)
        {
            throw new InvalidOperationException($"Resource groups left behind: {string.Join(", ", left)}; run tools/azure/cleanup.sh --yes --all.");
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

    // The records of a label on dev, oldest first.
    private List<JsonElement> RecordsOf(string label) =>
        Directory.GetFiles(Path.Combine(Records, DevName, label), "*-deploy.json").Order(StringComparer.Ordinal).Select(file =>
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

    // The value of an environment variable of the Container App's container (its Azure resource id).
    private string AppEnv(string appId, string name)
    {
        var result = _az.Run("resource", "show", "--ids", appId, "--query", $"properties.template.containers[0].env[?name=='{name}'].value | [0]", "-o", "tsv");
        return result.Code == 0 ? result.Output.Trim() : throw new InvalidOperationException($"az resource show {appId} failed: {result.Error}");
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

        // The deploy left a record naming the principal the run authenticated with (as Entra reports it) and a passed OQ for each workload.
        var firstRecord = Assert.Single(RecordsOf("1.0.0"));
        Assert.Equal("succeeded", firstRecord.GetProperty("outcome").GetString());
        Assert.Equal(System.Environment.GetEnvironmentVariable("ARM_CLIENT_ID"), firstRecord.GetProperty("identity").GetProperty("clientId").GetString());
        Assert.Equal(System.Environment.GetEnvironmentVariable("ARM_TENANT_ID"), firstRecord.GetProperty("identity").GetProperty("tenantId").GetString());
        Assert.Equal(["passed", "passed"], [Oq(firstRecord, 0), Oq(firstRecord, 1)]);
        Assert.Equal("passed", Iq(firstRecord)); // the deploy qualified itself: no drift, the pinned digest live

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
    }
}
