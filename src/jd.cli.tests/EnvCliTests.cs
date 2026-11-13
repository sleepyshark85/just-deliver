using System.Runtime.CompilerServices;
using jd.core.bp;
using jd.resolver.environment;
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

        /// <summary>How many times the run's backend factory built this backend.</summary>
        public int Built { get; set; }

        /// <summary>The output name that comes back as a secret from every deploy.</summary>
        public string? SecretOutput { get; set; }

        /// <summary>The stack whose deploy fails.</summary>
        public string? FailOn { get; set; }

        /// <summary>Runs inside every deploy: what happens to the world while the walk is in progress.</summary>
        public Action? DuringDeploy { get; set; }

        public Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken)
        {
            Calls.Add(package.StackName);
            DuringDeploy?.Invoke();
            if (package.StackName == FailOn)
            {
                throw new InvalidOperationException("provider exploded");
            }

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
        var code = await Cli.RunAsync(["env", .. args], stdout, stderr, CancellationToken.None, Composition.Connect(name => variables?.GetValueOrDefault(name)) with
        {
            Backend = () =>
            {
                backend.Built++;
                return backend;
            },
        });
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
        var descriptor = (await EnvironmentParser.LoadAsync(await Cli.ReadDocumentAsync(dev, CancellationToken.None), CancellationToken.None)).Value!;
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
        var descriptor = (await EnvironmentParser.LoadAsync(await Cli.ReadDocumentAsync(stage, CancellationToken.None), CancellationToken.None)).Value!;
        Assert.Equal(("stage", "stage"), (descriptor.Name, descriptor.Type));
        Assert.Equal(("dev", 1, true), (descriptor.Approval!.QualifiedOn, descriptor.Approval.Required, descriptor.Approval.SeparateFromDeployer));
        Assert.Equal(["00000000-0000-0000-0000-000000000000"], descriptor.Approval.Approvers);
        Assert.Equal(["_stage.stage.substrate.apps", "_stage.stage.substrate.database", "_stage.stage.substrate.group", "_stage.stage.substrate.logs"], backend.Calls.Order().ToList());
    }

    // The catalog declares the types (docs/architecture/components.md section 2): the definition's and the base's are checked before anything is
    // created, and each error names its file as typed (here relative to the working directory).
    [Fact]
    public async Task Up_of_an_undeclared_type_or_on_a_base_of_one_creates_nothing_and_names_the_type_and_the_declared_ones()
    {
        var backend = new OutputBackend();
        var shared = Path.Combine(_temp, "shared.env.yaml");
        await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", shared);
        backend.Calls.Clear();
        backend.Built = 0;
        var gold = Path.GetRelativePath(System.Environment.CurrentDirectory, Path.Combine(_temp, "gold.yaml"));
        await File.WriteAllTextAsync(gold, (await File.ReadAllTextAsync(Dev)).Replace("type: dev", "type: gold", StringComparison.Ordinal));
        var goldBase = Path.GetRelativePath(System.Environment.CurrentDirectory, Path.Combine(_temp, "gold.env.yaml"));
        await File.WriteAllTextAsync(goldBase, (await File.ReadAllTextAsync(shared)).Replace("type: shared", "type: gold", StringComparison.Ordinal));

        var definition = await RunAsync(backend, null, "up", gold, "--catalog", Catalog, "--base", shared, "--out", Path.Combine(_temp, "a.env.yaml"));
        var onBase = await RunAsync(backend, null, "up", Dev, "--catalog", Catalog, "--base", goldBase, "--out", Path.Combine(_temp, "b.env.yaml"));

        const string Error = "type: 'gold' is not an environment type of the catalog (declared: dev, prod, shared, stage).";
        Assert.False(Path.IsPathRooted(gold) || Path.IsPathRooted(goldBase));
        Assert.Equal((1, string.Empty, $"{gold}: {Error}{System.Environment.NewLine}"), definition);
        Assert.Equal((1, string.Empty, $"{goldBase}: {Error}{System.Environment.NewLine}"), onBase);
        Assert.Empty(backend.Calls);
        Assert.Equal(0, backend.Built);
    }

    [Fact]
    public async Task Up_prints_a_catalog_error_with_the_path_under_the_catalog_and_creates_nothing()
    {
        var backend = new OutputBackend();
        var catalog = Directory.CreateDirectory(Path.Combine(_temp, "catalog", "components")).Parent!.FullName;
        await File.WriteAllTextAsync(Path.Combine(catalog, "components", "oops.yaml"), "kind: Component\nname: oops\n");

        var (code, _, stderr) = await RunAsync(backend, null, "up", Shared, "--catalog", catalog, "--out", Path.Combine(_temp, "out.yaml"));

        Assert.Equal(1, code);
        Assert.Contains($"{Path.Combine(catalog, "components/oops.yaml")}: description: schema violation: PropertyRequired: #/description", stderr.Split('\n'));
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task An_existing_output_without_force_exits_2_before_anything_is_built_and_keeps_the_file()
    {
        var backend = new OutputBackend();
        var path = Path.Combine(_temp, "out.yaml");
        await File.WriteAllTextAsync(path, "old");

        var refused = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", path);

        Assert.Equal((2, string.Empty, $"jd: '{path}' already exists; pass --force to overwrite it.{System.Environment.NewLine}"), refused);
        Assert.Equal(0, backend.Built);
        Assert.Equal("old", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Force_overwrites_an_existing_output_with_the_whole_descriptor()
    {
        var backend = new OutputBackend();
        var path = Path.Combine(_temp, "out.yaml");
        await File.WriteAllTextAsync(path, new string('x', 10_000)); // longer than the descriptor: an overwrite that does not truncate leaves a tail

        var forced = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", path, "--force");

        Assert.Equal((0, $"{path}: environment shared (southeastasia, shared) written.{System.Environment.NewLine}", string.Empty), (forced.Code, forced.Out[forced.Out.IndexOf(path, StringComparison.Ordinal)..], forced.Err));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "golden", "shared.env.yaml")), await File.ReadAllBytesAsync(path));
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
    public async Task A_failed_node_stops_the_walk_names_the_node_and_writes_nothing()
    {
        var backend = new OutputBackend { FailOn = "_shared.shared.substrate.workspace" };
        var path = Path.Combine(_temp, "out.yaml");

        var (code, stdout, stderr) = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", path);

        Assert.Equal(1, code);
        Assert.Equal($"jd: stopped at @shared/shared/substrate/workspace: provider exploded{System.Environment.NewLine}", stderr);
        Assert.Contains("@shared/shared/substrate/workspace", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("written.", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
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
    [InlineData("env needs a command: up.")]
    [InlineData("unknown env command 'sideways'.", "sideways")]
    [InlineData("unknown env command '--force'.", "--force", "up")]
    [InlineData("expected exactly one definition file.", "up")]
    [InlineData("expected exactly one definition file.", "up", "a.yaml", "b.yaml", "--catalog", "c", "--out", "o.yaml")]
    [InlineData("env up needs --catalog and --out.", "up", "a.yaml", "--out", "o.yaml")]
    [InlineData("env up needs --catalog and --out.", "up", "a.yaml", "--catalog", "c")]
    [InlineData("--catalog needs a value.", "up", "a.yaml", "--out", "o.yaml", "--catalog")]
    [InlineData("--out needs a value.", "up", "a.yaml", "--catalog", "c", "--out")]
    [InlineData("--base needs a value.", "up", "a.yaml", "--catalog", "c", "--out", "o.yaml", "--base")]
    [InlineData("unknown option '--env'.", "up", "a.yaml", "--env", "e.yaml", "--catalog", "c", "--out", "o.yaml")]
    public async Task Usage_errors_exit_2_and_print_the_problem_and_the_usage(string problem, params string[] args)
    {
        var usage = new StringWriter();
        await Cli.RunAsync(["--help"], usage, TextWriter.Null);
        var backend = new OutputBackend();

        var result = await RunAsync(backend, null, args);

        Assert.Equal((2, string.Empty, $"jd: {problem}{System.Environment.NewLine}{usage}"), result);
        Assert.Equal(0, backend.Built);
    }

    // Each input is checked in turn, the definition, then the base, then the catalog, before anything is read.
    [Theory]
    [InlineData("definition", "cannot read '{missing}': not found or not a file.")]
    [InlineData("base", "cannot read '{missing}': not found or not a file.")]
    [InlineData("catalog", "cannot read '{missing}': not found or not a directory.")]
    public async Task A_missing_input_exits_2_naming_it_before_anything_is_built(string role, string problem)
    {
        var backend = new OutputBackend();
        var missing = Path.Combine(_temp, "missing");
        string Or(string name, string present) => role == name ? missing : present;

        var result = await RunAsync(backend, null, "up", Or("definition", Dev), "--catalog", Or("catalog", Catalog), "--base", Or("base", Shared), "--out", Path.Combine(_temp, "out.yaml"));

        Assert.Equal((2, string.Empty, $"jd: {problem.Replace("{missing}", missing, StringComparison.Ordinal)}{System.Environment.NewLine}"), result);
        Assert.Equal(0, backend.Built);
    }

    [Fact]
    public async Task A_definition_the_process_cannot_read_exits_2_with_the_path()
    {
        var path = Path.Combine(_temp, "locked.yaml");
        await File.WriteAllTextAsync(path, await File.ReadAllTextAsync(Shared));
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        if (CanRead(path))
        {
            return; // Running as a user that ignores file permissions (root).
        }

        var backend = new OutputBackend();

        var (code, stdout, stderr) = await RunAsync(backend, null, "up", path, "--catalog", Catalog, "--out", Path.Combine(_temp, "out.yaml"));

        Assert.Equal((2, string.Empty), (code, stdout));
        Assert.StartsWith("jd: ", stderr, StringComparison.Ordinal);
        Assert.Contains(path, stderr, StringComparison.Ordinal);
        Assert.Equal(0, backend.Built);
    }

    // The output is written after the walk: a directory that is not there is reported as a usage error, as today.
    [Fact]
    public async Task An_output_that_cannot_be_written_exits_2_with_the_path()
    {
        var path = Path.Combine(_temp, "no-such-directory", "out.yaml");

        var (code, _, stderr) = await RunAsync(new OutputBackend(), null, "up", Shared, "--catalog", Catalog, "--out", path);

        Assert.Equal(2, code);
        Assert.StartsWith("jd: ", stderr, StringComparison.Ordinal);
        Assert.Contains(path, stderr, StringComparison.Ordinal);
    }

    // The up-front check cannot see an --out that appears during the walk; writing must still refuse to overwrite it without --force.
    [Fact]
    public async Task An_output_that_appears_during_the_walk_is_not_overwritten_without_force()
    {
        var path = Path.Combine(_temp, "out.yaml");
        var backend = new OutputBackend { DuringDeploy = () => File.WriteAllText(path, "someone else's file") };

        var (code, _, stderr) = await RunAsync(backend, null, "up", Shared, "--catalog", Catalog, "--out", path);

        Assert.Equal(2, code);
        Assert.Contains($"jd: The file '{path}' already exists.", stderr, StringComparison.Ordinal);
        Assert.Equal("someone else's file", await File.ReadAllTextAsync(path));
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
}
