using jd.bp.pulumi;
using jd.core.bp;
using Microsoft.Extensions.DependencyInjection;

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

    protected static string Region => System.Environment.GetEnvironmentVariable("JD_REGION")
        ?? throw new InvalidOperationException("The Azure tests need JD_REGION (see docs/plans/status.md, Environment).");

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

    /// <summary>
    /// Every workload needs a runtime mapping. This test-only one points at a template with no resources, so a runtime deploy
    /// never reaches Azure; it declares the inputs and outputs the release block names.
    /// </summary>
    protected void WriteRuntimeStandIn()
    {
        Write("catalog/mappings/runtime.yaml", """
            kind: Mapping
            match: { kind: runtime }
            probe: { path: /health, expectedStatus: 200, timeoutSeconds: 60, intervalSeconds: 5 }
            release: { suffixInput: suffix, trafficInput: traffic, trafficOutput: traffic, revisionOutput: revision, fqdnOutput: fqdn, revisionKey: revisionName, latestKey: latestRevision, weightKey: weight }
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
              revision: stand-in
              fqdn: example.invalid
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

    /// <summary>The real Pulumi backend on a local file state under <see cref="Root"/>.</summary>
    protected IBackEndProvider CreateBackend()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterPulumiBackend(options =>
        {
            options.BackendUrl = "file://" + Path.Combine(Root, "state");
            options.ConfigPassPhrase = "test-passphrase";
            options.ScratchDirectory = Path.Combine(Root, "scratch");
        });
        Directory.CreateDirectory(Path.Combine(Root, "state"));
        return services.BuildServiceProvider().GetRequiredService<IBackEndProvider>();
    }

    protected static async Task<(int Code, string Output)> DeployAsync(IBackEndProvider backend, string workload, string environment, string catalog)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(["deploy", workload, "--env", environment, "--catalog", catalog], stdout, stderr, CancellationToken.None, backend);
        return (code, stdout + stderr.ToString());
    }
}
