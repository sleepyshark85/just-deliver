using jd.core.bp;
using jd.orchestrator;
using jd.resolver.catalog;

namespace jd.cli.tests;

/// <summary>
/// What the Azure tests that deploy a test-only catalog into one resource group share: a temporary working directory, the sandbox
/// <c>az</c> session, a Pulumi backend on a local file state, a writer for the catalog files, the <c>jd deploy</c> call and a teardown
/// that deletes the group and fails the test if it cannot. A subclass names the group through <see cref="Tag"/>.
/// </summary>
public abstract class ProbeAzureTest : IDisposable
{
    internal AzureCli Az { get; } = AzureCli.Login();

    protected string Root { get; } = Path.Combine(Path.GetTempPath(), "jd-azure-" + Guid.NewGuid().ToString("N"));

    protected string Suffix { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The slice the test belongs to, for example <c>s11</c>; it prefixes the group and environment names.</summary>
    protected abstract string Tag { get; }

    protected string ResourceGroup => $"rg-jd-{Tag}-{Suffix}";

    protected static Task<string> RegionAsync() => AzureCli.RegionAsync();

    public void Dispose()
    {
        try
        {
            DeleteResourceGroup();
        }
        finally
        {
            Az.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    // A failed teardown fails the test: leaving a resource group behind must never go unnoticed. A deploy that failed before it
    // created the group leaves nothing to delete, which "az group exists" reports.
    private void DeleteResourceGroup()
    {
        var exists = Az.Run("group", "exists", "--name", ResourceGroup, "--subscription", Az.Subscription);
        if (exists.Code == 0 && exists.Output.Trim() == "false")
        {
            return;
        }

        if (exists.Code != 0)
        {
            throw new InvalidOperationException($"Could not check resource group {ResourceGroup}: {exists.Error}");
        }

        var delete = Az.Run("group", "delete", "--name", ResourceGroup, "--subscription", Az.Subscription, "--yes");
        if (delete.Code != 0)
        {
            throw new InvalidOperationException($"Could not delete resource group {ResourceGroup}; run tools/azure/cleanup.sh --yes. {delete.Error}");
        }
    }

    protected string Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>The runtime stand-in has no revision to ask, so its probe passes; the real HTTP probe is exercised against a real app by <see cref="EnvUpAzureTests"/>.</summary>
    private sealed class PassingProbe : IRevisionProbe
    {
        public Task<ProbeResult> ProbeAsync(string fqdn, Probe probe, CancellationToken cancellationToken) => Task.FromResult(new ProbeResult(true, "stand-in"));
    }

    /// <summary>
    /// The resource group the test creates, as a shared resource the probe component reads through <c>uses</c> as
    /// <c>${probe-group.name}</c>: the descriptor carries its value, and no <c>env up</c> deploys it here.
    /// </summary>
    protected void WriteProbeGroup()
    {
        Write("catalog/shared/probe-group.yaml", """
            kind: SharedResource
            name: probe-group
            description: Test-only; its name is the descriptor's value.
            scope: environment
            nodes:
              stand-in:
                template: azure/resource-group
                config: { resourceGroupName: unused, location: '${env.region}' }
            exports:
              name: ${stand-in.resourceGroupName}
            """);
    }

    /// <summary>
    /// Every workload needs a runtime. This test-only one points at a template with no resources, so the release flow
    /// deploys it without reaching Azure (<see cref="DeployAsync"/> gives the flow a probe that passes: there is no app behind the
    /// stand-in's fqdn); it declares the inputs and outputs the release block names.
    /// </summary>
    protected void WriteRuntimeStandIn()
    {
        Write("catalog/runtimes/test-runtime.yaml", """
            kind: Runtime
            name: test-runtime
            description: Test-only runtime stand-in.
            implementations:
              default:
                probe: { path: /health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5 }
                release: { suffixInput: suffix, trafficInput: traffic, trafficOutput: traffic, suffixOutput: suffix, revisionOutput: revision, fqdnOutput: fqdn, revisionKey: revisionName, latestKey: latestRevision, weightKey: weight, resourceIdOutput: appId }
                nodes:
                  runtime:
                    template: test/runtime-stand-in
                    config: {}
            """);
        Write("catalog/templates/test/runtime-stand-in/Pulumi.yaml", """
            name: runtime-stand-in
            runtime: yaml
            configuration:
              suffix:
                type: String
              traffic:
                type: List<Object>
            outputs:
              traffic: ${traffic}
              suffix: ${suffix}
              revision: stand-in
              fqdn: example.invalid
              appId: stand-in-id
            """);
    }

    /// <summary>Copies the named templates from the catalog under test into the test catalog.</summary>
    protected void CopyTemplates(params string[] templates)
    {
        foreach (var template in templates)
        {
            var from = Path.Combine(AppContext.BaseDirectory, "catalog", "templates", "azure", template, "Pulumi.yaml");
            Write($"catalog/templates/azure/{template}/Pulumi.yaml", File.ReadAllText(from));
        }
    }

    /// <summary>The real Pulumi backend on jd's state in a data directory under <see cref="Root"/>.</summary>
    protected IBackEndProvider CreateBackend() => Cli.CreatePulumiBackend(AzureCli.Variables(Root));

    protected static async Task<(int Code, string Output)> DeployAsync(IBackEndProvider backend, string workload, string environment, string catalog)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(["deploy", workload, "--env", environment, "--catalog", catalog], stdout, stderr, CancellationToken.None, backend, probe: new PassingProbe());
        return (code, stdout + stderr.ToString());
    }
}
