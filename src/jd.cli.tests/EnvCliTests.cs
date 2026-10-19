using System.Runtime.CompilerServices;
using jd.core.bp;
using Xunit;

namespace jd.cli.tests;

// `jd env up` in-process against the seed catalog and sample definitions with a fake backend that answers each node with outputs.
public sealed class EnvCliTests : IDisposable
{
    private static readonly string Catalog = Path.Combine(AppContext.BaseDirectory, "catalog");
    private static readonly string Shared = Path.Combine(AppContext.BaseDirectory, "samples", "environments", "shared.yaml");
    private static readonly string Dev = Path.Combine(AppContext.BaseDirectory, "samples", "environments", "dev.yaml");

    private static readonly string Stage = Path.Combine(AppContext.BaseDirectory, "samples", "environments", "stage.yaml");

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
            ["logs"] = new() { ["settingId"] = "/apps/id/diagnostic" },
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

    [Fact]
    public async Task Up_deploys_the_substrate_and_writes_a_descriptor_that_loads_and_serves_as_the_base_of_the_next()
    {
        var backend = new OutputBackend();
        var shared = Path.Combine(_temp, "shared.env.yaml");
        var dev = Path.Combine(_temp, "dev.env.yaml");

        var first = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", shared);
        var second = await RunAsync(backend, null, "up", Dev, "--catalog", Catalog, "--base", shared, "--out", dev);

        Assert.Equal((0, 0), (first.Code, second.Code));
        Assert.Equal(["_dev.dev.substrate.apps", "_dev.dev.substrate.database", "_dev.dev.substrate.group", "_dev.dev.substrate.logs", "_shared.shared.substrate.cosmos", "_shared.shared.substrate.group", "_shared.shared.substrate.workspace"], backend.Calls.Order().ToList());
        var descriptor = (await jd.resolver.environment.EnvironmentFile.LoadAsync(dev)).Descriptor!;
        Assert.Equal(("dev", "southeastasia"), (descriptor.Name, descriptor.Region));
        Assert.Equal("cosmos-1", descriptor.Values["cosmos-account.name"]);
        Assert.Equal("db-dev", descriptor.Values["database.databaseName"]);
        Assert.Contains($"{dev}: environment dev (southeastasia, dev) written.", second.Out);
    }

    // What jd writes is pinned byte for byte (docs/architecture/documents.md section 8). Regenerate with JD_UPDATE_GOLDEN=1 and review the diff.
    [Fact]
    public async Task Up_of_the_sample_shared_and_dev_writes_the_committed_golden_descriptors()
    {
        var backend = new OutputBackend();
        var shared = Path.Combine(_temp, "shared.env.yaml");
        var dev = Path.Combine(_temp, "dev.env.yaml");

        var first = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", shared);
        var second = await RunAsync(backend, null, "up", Dev, "--catalog", Catalog, "--base", shared, "--out", dev);

        Assert.Equal((0, 0), (first.Code, second.Code));
        await AssertGoldenAsync(shared, "shared.env.yaml");
        await AssertGoldenAsync(dev, "dev.env.yaml");
    }

    private static async Task AssertGoldenAsync(string written, string golden)
    {
        var actual = await File.ReadAllTextAsync(written);
        if (System.Environment.GetEnvironmentVariable("JD_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(GoldenSource(golden), actual);
        }

        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", golden)), actual);
    }

    private static string GoldenSource(string name, [CallerFilePath] string testFile = "") => Path.Combine(Path.GetDirectoryName(testFile)!, "golden", name);

    [Fact]
    public async Task Up_of_the_protected_stage_writes_its_policy_into_the_descriptor_and_a_protected_definition_without_one_deploys_nothing()
    {
        var backend = new OutputBackend();
        var shared = Path.Combine(_temp, "shared.env.yaml");
        var stage = Path.Combine(_temp, "stage.env.yaml");
        await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", shared);
        backend.Calls.Clear();
        var open = Path.Combine(_temp, "open.yaml");
        var definition = await File.ReadAllTextAsync(Stage);
        await File.WriteAllTextAsync(open, definition[..definition.IndexOf("approval:", StringComparison.Ordinal)]);

        var refused = await RunAsync(backend, null, "up", open, "--catalog", Catalog, "--base", shared, "--out", Path.Combine(_temp, "open.env.yaml"));
        var refusedCalls = backend.Calls.Count;
        var up = await RunAsync(backend, null, "up", Stage, "--catalog", Catalog, "--base", shared, "--out", stage);

        Assert.Equal((1, 0), (refused.Code, up.Code));
        Assert.Equal(0, refusedCalls);
        Assert.Contains($"{open}: approval: environment type 'stage' requires an 'approval' block", refused.Err);
        var descriptor = (await jd.resolver.environment.EnvironmentFile.LoadAsync(stage)).Descriptor!;
        Assert.Equal(("stage", "stage"), (descriptor.Name, descriptor.Type));
        Assert.Equal(("dev", 1, true), (descriptor.Approval!.QualifiedOn, descriptor.Approval.Required, descriptor.Approval.SeparateFromDeployer));
        Assert.Equal(["00000000-0000-0000-0000-000000000000"], descriptor.Approval.Approvers);
        Assert.Equal(["_stage.stage.substrate.apps", "_stage.stage.substrate.database", "_stage.stage.substrate.group", "_stage.stage.substrate.logs"], backend.Calls.Order().ToList());
    }

    // The catalog declares the types (docs/architecture/components.md section 2): the definition's and the base's are checked before anything is created.
    [Fact]
    public async Task Up_of_an_undeclared_type_or_on_a_base_of_one_creates_nothing_and_names_the_type_and_the_declared_ones()
    {
        var backend = new OutputBackend();
        var shared = Path.Combine(_temp, "shared.env.yaml");
        await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", shared);
        backend.Calls.Clear();
        var gold = Path.Combine(_temp, "gold.yaml");
        await File.WriteAllTextAsync(gold, (await File.ReadAllTextAsync(Dev)).Replace("type: dev", "type: gold", StringComparison.Ordinal));
        var goldBase = Path.Combine(_temp, "gold.env.yaml");
        await File.WriteAllTextAsync(goldBase, (await File.ReadAllTextAsync(shared)).Replace("type: shared", "type: gold", StringComparison.Ordinal));

        var definition = await RunAsync(backend, null, "up", gold, "--catalog", Catalog, "--base", shared, "--out", Path.Combine(_temp, "a.env.yaml"));
        var onBase = await RunAsync(backend, null, "up", Dev, "--catalog", Catalog, "--base", goldBase, "--out", Path.Combine(_temp, "b.env.yaml"));

        Assert.Equal((1, 1), (definition.Code, onBase.Code));
        Assert.Empty(backend.Calls);
        const string Error = "type: 'gold' is not an environment type of the catalog (declared: dev, prod, shared, stage).";
        Assert.Contains($"{gold}: {Error}", definition.Err);
        Assert.Contains($"{goldBase}: {Error}", onBase.Err);
    }

    [Fact]
    public async Task An_existing_output_is_refused_before_anything_is_created_unless_forced()
    {
        var backend = new OutputBackend();
        var path = Path.Combine(_temp, "out.yaml");
        await File.WriteAllTextAsync(path, "old");

        var refused = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", path);
        var forced = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", path, "--force");

        Assert.Equal(2, refused.Code);
        Assert.Contains("already exists", refused.Err);
        Assert.Equal(0, forced.Code);
        Assert.StartsWith("kind: Environment", await File.ReadAllTextAsync(path));
        Assert.Equal(3, backend.Calls.Count);
    }

    [Fact]
    public async Task The_region_is_the_definitions_and_neither_JD_REGION_nor_a_region_option_has_a_say()
    {
        var backend = new OutputBackend();
        var path = Path.Combine(_temp, "out.yaml");

        var variable = await RunAsync(backend, new() { ["JD_REGION"] = "region-2" }, "up", Shared, "--catalog", Catalog, "--out", path);
        var option = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", path, "--force", "--region", "region-2");

        Assert.Equal(0, variable.Code);
        Assert.Contains("region: southeastasia", await File.ReadAllTextAsync(path));
        Assert.Equal(2, option.Code);
        Assert.Contains("unknown option '--region'", option.Err);
    }

    [Fact]
    public async Task A_definition_that_cannot_produce_its_descriptor_deploys_nothing()
    {
        var backend = new OutputBackend();
        var shared = Path.Combine(_temp, "shared.env.yaml");
        var dev = Path.Combine(_temp, "dev.env.yaml");
        await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", shared);
        await RunAsync(backend, null, "up", Dev, "--catalog", Catalog, "--base", shared, "--out", dev);
        backend.Calls.Clear();
        var broken = Path.Combine(_temp, "broken.yaml");
        await File.WriteAllTextAsync(broken, (await File.ReadAllTextAsync(Dev)).Replace("offers: [container-app, database", "offers: [container-app, nosql"));

        var collision = await RunAsync(backend, null, "up", Dev, "--catalog", Catalog, "--base", dev, "--out", Path.Combine(_temp, "a.yaml"));
        var unknown = await RunAsync(backend, null, "up", broken, "--catalog", Catalog, "--base", shared, "--out", Path.Combine(_temp, "b.yaml"));
        var noBase = await RunAsync(backend, null, "up", Dev, "--catalog", Catalog, "--out", Path.Combine(_temp, "c.yaml"));

        Assert.Equal((1, 1, 1), (collision.Code, unknown.Code, noBase.Code));
        Assert.Contains("offers: part 'apps-env' is already in the base descriptor's values", collision.Err);
        Assert.Contains("offers[1]: 'nosql' is not a component or runtime in the catalog.", unknown.Err);
        Assert.Contains("the offers reach shared resource 'cosmos-account', but there is no base descriptor", noBase.Err);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task A_value_that_needs_a_secret_output_fails_and_nothing_is_written_or_printed()
    {
        var backend = new OutputBackend { SecretOutput = "documentEndpoint" };
        var path = Path.Combine(_temp, "out.yaml");

        var (code, stdout, stderr) = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", path);

        Assert.Equal(1, code);
        Assert.Contains("values.cosmos-account.endpoint", stderr);
        Assert.Contains("missing, null or secret", stderr);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain("https://cosmos-1/", stdout + stderr);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("up")]
    public async Task Usage_problems_exit_with_2(string command)
    {
        var (code, _, err) = await RunAsync(new OutputBackend(), null, command);

        Assert.Equal(2, code);
        Assert.Contains("jd:", err);
    }
}
