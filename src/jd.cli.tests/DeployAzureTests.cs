using jd.bp.pulumi;
using jd.core.bp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace jd.cli.tests;

/// <summary>
/// Real resources, free tier only: a test-only catalog whose one type maps to a resource group and a Log Analytics workspace
/// (daily cap 0.15 GB, the template's default 30-day retention) that references the group's outputs. Deploys it with the real
/// Pulumi backend on a local file state, deploys again for zero changes, and always deletes the resource group.
/// Run with <c>tools/verify.sh --azure</c>; needs the sandbox team identity (<c>ARM_CLIENT_ID</c>, <c>ARM_CLIENT_SECRET</c>, <c>ARM_TENANT_ID</c>, <c>ARM_SUBSCRIPTION_ID</c>) in the environment, <c>JD_REGION</c> and the pulumi CLI.
/// </summary>
[Trait("Category", "Azure")]
public sealed class DeployAzureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jd-azure-" + Guid.NewGuid().ToString("N"));
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly AzureCli _az = AzureCli.Login();

    private string ResourceGroup => "rg-jd-s11-" + _suffix;

    public void Dispose()
    {
        try
        {
            DeleteResourceGroup();
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

    // A failed teardown fails the test: leaving a resource group behind must never go unnoticed. A deploy that failed before it
    // created the group leaves nothing to delete, which "az group exists" reports.
    private void DeleteResourceGroup()
    {
        var exists = _az.Run("group", "exists", "--name", ResourceGroup, "--subscription", _az.Subscription);
        if (exists.Code == 0 && exists.Output.Trim() == "false")
        {
            return;
        }

        if (exists.Code != 0)
        {
            throw new InvalidOperationException($"Could not check resource group {ResourceGroup}: {exists.Error}");
        }

        var delete = _az.Run("group", "delete", "--name", ResourceGroup, "--subscription", _az.Subscription, "--yes");
        if (delete.Code != 0)
        {
            throw new InvalidOperationException($"Could not delete resource group {ResourceGroup}; run tools/azure/cleanup.sh --yes. {delete.Error}");
        }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Deploy_creates_a_group_and_a_workspace_in_it_and_a_second_deploy_changes_nothing()
    {
        var region = System.Environment.GetEnvironmentVariable("JD_REGION")
            ?? throw new InvalidOperationException("The Azure tests need JD_REGION (see docs/plans/status.md, Environment).");
        var catalog = Path.Combine(_root, "catalog");
        Write("catalog/catalog.yaml", "kind: Catalog\nversion: \"test\"\n");
        Write("catalog/types/probe.yaml", "kind: ResourceType\nname: probe\ndescription: Test-only type.\nclasses: [standard]\nexports: [workspace]\n");
        Write("catalog/naming.yaml", "kind: Naming\nrules:\n  law:\n    pattern: \"law-{env}-{hash}\"\n    maxLength: 63\n    allowed: \"[a-z0-9-]\"\n");
        Write("catalog/mappings/probe.yaml", """
            kind: Mapping
            match: { type: probe, class: standard }
            nodes:
              group:
                template: azure/resource-group
                config:
                  resourceGroupName: ${env.probeGroup}
                  location: ${env.region}
                  tags: { project: just-deliver-mvp }
              workspace:
                template: azure/log-analytics
                config:
                  resourceGroupName: ${group.resourceGroupName}
                  location: ${group.location}
                  workspaceName: ${name('law')}
                  dailyQuotaGb: 0.15
            exports:
              workspace: ${workspace.workspaceName}
            """);
        // The walk does not deploy the runtime node, so this test-only stand-in never reaches Azure; every workload needs a runtime mapping.
        Write("catalog/mappings/runtime.yaml", """
            kind: Mapping
            match: { kind: runtime }
            nodes:
              runtime:
                template: azure/resource-group
                config:
                  resourceGroupName: ${env.probeGroup}
                  location: ${env.region}
                  tags: { project: just-deliver-mvp }
            """);
        foreach (var template in new[] { "resource-group", "log-analytics" })
        {
            var from = Path.Combine(AppContext.BaseDirectory, "catalog", "templates", "azure", template, "Pulumi.yaml");
            Write($"catalog/templates/azure/{template}/Pulumi.yaml", File.ReadAllText(from));
        }

        var environment = Write("environment.yaml", $"kind: Environment\nname: s11-{_suffix}\nregion: {region}\ntier: team\nvalues:\n  probeGroup: {ResourceGroup}\n");
        var workload = Write("workload.yaml", "apiVersion: just-deliver/v1\nkind: Workload\nmetadata: { name: s11-probe, team: platform-team }\ncontainer:\n  image: example.invalid/probe:1\nrequires:\n  - type: probe\n");

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

        var (firstCode, first) = await DeployAsync(backend, workload, environment, catalog);
        Assert.True(firstCode == 0, first);
        var group = first.IndexOf("s11-probe/s11-" + _suffix + "/probe/group: deployed", StringComparison.Ordinal);
        var workspace = first.IndexOf("s11-probe/s11-" + _suffix + "/probe/workspace: deployed", StringComparison.Ordinal);
        Assert.True(group >= 0 && workspace > group, "group then workspace, both deployed:\n" + first);

        // The workspace was created in the group by name, so the group's output reached the workspace node.
        var workspaceStack = $"s11-probe.s11-{_suffix}.probe.workspace";
        var content = File.ReadAllText(Path.Combine(catalog, "templates", "azure", "log-analytics", "Pulumi.yaml"));
        var outputs = await backend.GetOutputsAsync(workspaceStack, content, CancellationToken.None);
        Assert.NotNull(outputs);
        Assert.Contains($"/resourceGroups/{ResourceGroup}/", outputs["workspaceId"].Value, StringComparison.OrdinalIgnoreCase);

        var (secondCode, second) = await DeployAsync(backend, workload, environment, catalog);
        Assert.True(secondCode == 0, second);
        Assert.Contains("probe/group: unchanged (no changes", second);
        Assert.Contains("probe/workspace: unchanged (no changes", second);
    }

    private static async Task<(int Code, string Output)> DeployAsync(IBackEndProvider backend, string workload, string environment, string catalog)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(["deploy", workload, "--env", environment, "--catalog", catalog], stdout, stderr, CancellationToken.None, backend);
        return (code, stdout + stderr.ToString());
    }
}
