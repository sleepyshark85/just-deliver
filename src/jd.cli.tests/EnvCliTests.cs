using jd.core.bp;
using Xunit;

namespace jd.cli.tests;

// `jd env up` in-process against the seed catalog and sample definitions with a fake backend that answers each node with outputs.
public sealed class EnvCliTests : IDisposable
{
    private static readonly string Catalog = Path.Combine(AppContext.BaseDirectory, "catalog");
    private static readonly string Shared = Path.Combine(AppContext.BaseDirectory, "samples", "environments", "shared.yaml");
    private static readonly string Dev = Path.Combine(AppContext.BaseDirectory, "samples", "environments", "dev.yaml");

    private readonly string _temp = Directory.CreateTempSubdirectory("jd-env-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    // Outputs by the node name at the end of the stack name.
    private sealed class OutputBackend : IBackEndProvider
    {
        private static readonly Dictionary<string, Dictionary<string, string>> ByNode = new()
        {
            ["group"] = new() { ["resourceGroupName"] = "rg-1", ["location"] = "region-1" },
            ["workspace"] = new() { ["workspaceId"] = "/ws/id", ["workspaceName"] = "law-1", ["workspaceCustomerId"] = "cust-1" },
            ["cosmos"] = new() { ["accountId"] = "/cosmos/id", ["accountName"] = "cosmos-1", ["documentEndpoint"] = "https://cosmos-1/" },
            ["apps"] = new() { ["environmentId"] = "/apps/id", ["defaultDomain"] = "dev.example.io" },
            ["database"] = new() { ["databaseId"] = "/db/id", ["databaseName"] = "db-dev" },
        };

        public List<string> Calls { get; } = [];

        /// <summary>The output name that comes back as a secret from every deploy.</summary>
        public string? SecretOutput { get; set; }

        public Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken)
        {
            Calls.Add(package.StackName);
            var outputs = ByNode[package.StackName[(package.StackName.LastIndexOf('.') + 1)..]].ToDictionary(o => o.Key, o => new ConfigEntry(o.Value, o.Key == SecretOutput));
            return Task.FromResult(new DeploymentResult { Outputs = outputs, Summary = new() { ["Create"] = 1 } });
        }

        public Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string stackName, string deploymentContent, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(OutputBackend backend, Dictionary<string, string>? variables, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(["env", .. args], stdout, stderr, CancellationToken.None, backend, name => variables?.GetValueOrDefault(name));
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static readonly Dictionary<string, string> Region = new() { ["JD_REGION"] = "region-1" };

    [Fact]
    public async Task Up_deploys_the_substrate_and_writes_a_descriptor_that_loads_and_serves_as_the_base_of_the_next()
    {
        var backend = new OutputBackend();
        var shared = Path.Combine(_temp, "shared.env.yaml");
        var dev = Path.Combine(_temp, "dev.env.yaml");

        var first = await RunAsync(backend, Region, "up", Shared, "--catalog", Catalog, "--out", shared);
        var second = await RunAsync(backend, Region, "up", Dev, "--catalog", Catalog, "--base", shared, "--out", dev);

        Assert.Equal((0, 0), (first.Code, second.Code));
        Assert.Equal(["_dev.dev.substrate.apps", "_dev.dev.substrate.database", "_dev.dev.substrate.group", "_shared.shared.substrate.cosmos", "_shared.shared.substrate.group", "_shared.shared.substrate.workspace"], backend.Calls.Order().ToList());
        var descriptor = (await jd.resolver.environment.EnvironmentFile.LoadAsync(dev)).Descriptor!;
        Assert.Equal(("dev", "region-1"), (descriptor.Name, descriptor.Region));
        Assert.Equal("cosmos-1", descriptor.Values["cosmos.accountName"]);
        Assert.Equal("db-dev", descriptor.Values["cosmos.databaseName"]);
        Assert.Contains($"{dev}: environment dev", second.Out);
    }

    [Fact]
    public async Task An_existing_output_is_refused_before_anything_is_created_unless_forced()
    {
        var backend = new OutputBackend();
        var path = Path.Combine(_temp, "out.yaml");
        await File.WriteAllTextAsync(path, "old");

        var refused = await RunAsync(backend, Region, "up", Shared, "--catalog", Catalog, "--out", path);
        var forced = await RunAsync(backend, Region, "up", Shared, "--catalog", Catalog, "--out", path, "--force");

        Assert.Equal(2, refused.Code);
        Assert.Contains("already exists", refused.Err);
        Assert.Equal(0, forced.Code);
        Assert.StartsWith("kind: Environment", await File.ReadAllTextAsync(path));
        Assert.Equal(3, backend.Calls.Count);
    }

    [Fact]
    public async Task The_region_comes_from_the_option_or_JD_REGION()
    {
        var backend = new OutputBackend();
        var path = Path.Combine(_temp, "out.yaml");

        var none = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", path);
        var option = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", path, "--region", "region-2");

        Assert.Equal(2, none.Code);
        Assert.Contains("JD_REGION", none.Err);
        Assert.Equal(0, option.Code);
        Assert.Contains("region: region-2", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_definition_that_cannot_produce_its_descriptor_deploys_nothing()
    {
        var backend = new OutputBackend();
        var shared = Path.Combine(_temp, "shared.env.yaml");
        await RunAsync(backend, Region, "up", Shared, "--catalog", Catalog, "--out", shared);
        backend.Calls.Clear();
        var clash = Path.Combine(_temp, "clash.yaml");
        await File.WriteAllTextAsync(clash, (await File.ReadAllTextAsync(Dev)).Replace("databaseName:", "accountName:"));
        var broken = Path.Combine(_temp, "broken.yaml");
        await File.WriteAllTextAsync(broken, (await File.ReadAllTextAsync(Dev)).Replace("resource.substrate.databaseId", "resource.substrate.nope"));

        var collision = await RunAsync(backend, Region, "up", clash, "--catalog", Catalog, "--base", shared, "--out", Path.Combine(_temp, "a.yaml"));
        var unknown = await RunAsync(backend, Region, "up", broken, "--catalog", Catalog, "--base", shared, "--out", Path.Combine(_temp, "b.yaml"));
        var noBase = await RunAsync(backend, Region, "up", Dev, "--catalog", Catalog, "--out", Path.Combine(_temp, "c.yaml"));

        Assert.Equal((1, 1, 1), (collision.Code, unknown.Code, noBase.Code));
        Assert.Contains("values.cosmos.accountName: 'cosmos.accountName' is already a value of the base descriptor", collision.Err);
        Assert.Contains("values.cosmos.databaseId", unknown.Err);
        Assert.Contains("'env.shared.resourceGroup' is not a value of environment 'dev'", noBase.Err);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_value_that_needs_a_secret_output_fails_and_nothing_is_written_or_printed()
    {
        var backend = new OutputBackend { SecretOutput = "documentEndpoint" };
        var path = Path.Combine(_temp, "out.yaml");

        var (code, stdout, stderr) = await RunAsync(backend, Region, "up", Shared, "--catalog", Catalog, "--out", path);

        Assert.Equal(1, code);
        Assert.Contains("values.cosmos.endpoint", stderr);
        Assert.Contains("missing, null or secret", stderr);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain("https://cosmos-1/", stdout + stderr);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("up")]
    public async Task Usage_problems_exit_with_2(string command)
    {
        var (code, _, err) = await RunAsync(new OutputBackend(), Region, command);

        Assert.Equal(2, code);
        Assert.Contains("jd:", err);
    }
}
