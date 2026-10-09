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
/// without throughput of their own, so the free-tier RU/s budget holds, and a second deploy changes nothing. It is one scenario because
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

    // Every node line reports no changes: the second run found everything as the first left it.
    private static void AssertNothingChanged(string output, int nodes)
    {
        Assert.Equal(nodes, output.Split('\n').Count(line => line.Contains(": unchanged (no changes", StringComparison.Ordinal)));
        Assert.DoesNotContain(": deployed", output);
    }

    private static async Task<(int Code, string Output)> ReleaseAsync(IBackEndProvider backend, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(["release", .. args], stdout, stderr, CancellationToken.None, backend);
        return (code, stdout + stderr.ToString());
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

        // The sample release set on dev: two workloads, one container each in the dev database, the grants waiting for the runtime.
        var releaseSet = Path.Combine(_root, "release.yaml");
        var (createCode, createOutput) = await ReleaseAsync(backend, "create", Path.Combine(AppContext.BaseDirectory, "samples", "releases", "sample.manifest.yaml"), "--out", releaseSet);
        Assert.True(createCode == 0, createOutput);
        var deploy = new[] { "deploy", releaseSet, "--env", devDescriptor, "--catalog", Path.Combine(AppContext.BaseDirectory, "catalog") };
        var (releaseCode, releaseOutput) = await ReleaseAsync(backend, deploy);
        Assert.True(releaseCode == 0, releaseOutput);
        Assert.Equal(2, releaseOutput.Split('\n').Count(line => line.Contains("/database/access: waiting for runtime", StringComparison.Ordinal)));

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
        AssertNothingChanged(againRelease, 4);
    }
}
