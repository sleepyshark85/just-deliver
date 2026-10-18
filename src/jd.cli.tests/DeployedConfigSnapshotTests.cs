using System.Runtime.CompilerServices;
using jd.core.bp;
using jd.definitionvalidator;
using jd.orchestrator;
using jd.resolver.catalog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.cli.tests;

// The refactor's acceptance (docs/architecture/components.md section 5): `env up` of the sample shared, dev and stage definitions, then
// `jd deploy` of the seed workload on dev, against a fake backend whose outputs are deterministic. Every package the backend is handed
// (stack, every config entry, its secret flag) is compared with a committed snapshot, so a change in what reaches Azure shows in the diff.
// Regenerate with JD_UPDATE_GOLDEN=1 and review the diff.
public sealed class DeployedConfigSnapshotTests : IDisposable
{
    private static readonly string Catalog = Path.Combine(AppContext.BaseDirectory, "catalog");
    private static readonly string Environments = Path.Combine(AppContext.BaseDirectory, "samples", "environments");
    private static readonly string Workload = Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml");
    private static readonly Dictionary<string, string> Variables = new() { ["JD_REGION"] = "region-1" };

    private readonly string _temp = Directory.CreateTempSubdirectory("jd-snapshot-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    // Answers every output the template declares with "<stack>/<output>"; nothing is deployed before the run, and every probe passes.
    private sealed class SnapshotBackend : IBackEndProvider, IRevisionProbe
    {
        public List<DeploymentPackage> Packages { get; } = [];

        public Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken)
        {
            Packages.Add(package);
            var outputs = ((YamlSchemaValidator.ParseYaml(package.DeploymentContent) as JObject)?["outputs"] as JObject)?.Properties()
                .ToDictionary(p => p.Name, p => new ConfigEntry($"{package.StackName}/{p.Name}")) ?? [];
            return Task.FromResult(new DeploymentResult { Outputs = outputs, Summary = new() { ["Create"] = 1 } });
        }

        public Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string stackName, string deploymentContent, CancellationToken cancellationToken) =>
            Task.FromResult<Dictionary<string, ConfigEntry>?>(null);

        public Task<ProbeResult> ProbeAsync(string fqdn, Probe probe, CancellationToken cancellationToken) => Task.FromResult(new ProbeResult(true, "HTTP 200"));
    }

    private static async Task Run(SnapshotBackend backend, params string[] args)
    {
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(args, new StringWriter(), stderr, CancellationToken.None, backend, name => Variables.GetValueOrDefault(name), backend);
        Assert.True(code == 0, $"jd {string.Join(' ', args)}: exit {code}: {stderr}");
    }

    [Fact]
    public async Task Every_package_handed_to_the_backend_matches_the_committed_snapshot()
    {
        var backend = new SnapshotBackend();
        var shared = Path.Combine(_temp, "shared.env.yaml");
        var dev = Path.Combine(_temp, "dev.env.yaml");

        await Run(backend, "env", "up", Path.Combine(Environments, "shared.yaml"), "--catalog", Catalog, "--out", shared);
        await Run(backend, "env", "up", Path.Combine(Environments, "dev.yaml"), "--catalog", Catalog, "--base", shared, "--out", dev);
        await Run(backend, "env", "up", Path.Combine(Environments, "stage.yaml"), "--catalog", Catalog, "--base", shared, "--out", Path.Combine(_temp, "stage.env.yaml"));
        await Run(backend, "deploy", Workload, "--env", dev, "--catalog", Catalog);

        // In call order (a runtime is deployed twice: dark, then the shift); config entries by key.
        var actual = new JArray(backend.Packages.Select(p => new JObject
        {
            ["stack"] = p.StackName,
            ["config"] = new JObject(p.DeploymentParameters.OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new JProperty(e.Key, new JObject { ["value"] = e.Value.Value, ["secret"] = e.Value.IsSecret }))),
        })).ToString(Formatting.Indented) + "\n";
        if (System.Environment.GetEnvironmentVariable("JD_UPDATE_GOLDEN") == "1")
        {
            await File.WriteAllTextAsync(SnapshotSource(), actual);
        }

        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "deployed-config.json")), actual);
    }

    private static string SnapshotSource([CallerFilePath] string testFile = "") => Path.Combine(Path.GetDirectoryName(testFile)!, "golden", "deployed-config.json");
}
