using jd.bp.pulumi;
using jd.core.bp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace jd.cli.tests;

/// <summary>
/// Real resources, free tier only: a test-only catalog whose one type maps to a resource group, a capped Log Analytics workspace, a
/// Container Apps environment and a <c>container-app</c> running the public sample image on Consumption (0.25 vCPU, 0.5Gi, scale to
/// zero, one replica at most). Every value comes from the test catalog, none from the template. Deploys it with the real Pulumi backend
/// on a local file state, checks revision mode, scale, identity and the variable through <c>az</c>, deploys again for zero changes, and
/// always deletes the resource group. It creates no Cosmos account, so it can run next to the env-up test.
/// Run with <c>tools/verify.sh --azure</c>; needs the sandbox team identity (<c>ARM_CLIENT_ID</c>, <c>ARM_CLIENT_SECRET</c>, <c>ARM_TENANT_ID</c>, <c>ARM_SUBSCRIPTION_ID</c>) in the environment, <c>JD_REGION</c> and the pulumi CLI.
/// </summary>
[Trait("Category", "Azure")]
public sealed class ContainerAppAzureTests : IDisposable
{
    private const string Image = "ghcr.io/sleepyshark85/just-deliver-sample-app@sha256:267b1385d6102da0d5693506e740d509a40b654e6daa2a99c1169938d182cab9";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jd-azure-" + Guid.NewGuid().ToString("N"));
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly AzureCli _az = AzureCli.Login();

    private string ResourceGroup => "rg-jd-s15a-" + _suffix;

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

    // The value of a JMESPath query on the resource, as tsv.
    private string Show(string resourceId, string query)
    {
        var result = _az.Run("resource", "show", "--ids", resourceId, "--query", query, "-o", "tsv");
        return result.Code == 0 ? result.Output.Trim() : throw new InvalidOperationException($"az resource show {resourceId} failed: {result.Error}");
    }

    [Fact]
    public async Task Deploy_creates_a_multi_revision_app_scaled_to_zero_with_an_identity_and_the_variable_and_a_second_deploy_changes_nothing()
    {
        var region = System.Environment.GetEnvironmentVariable("JD_REGION")
            ?? throw new InvalidOperationException("The Azure tests need JD_REGION (see docs/plans/status.md, Environment).");
        var catalog = Path.Combine(_root, "catalog");
        Write("catalog/catalog.yaml", "kind: Catalog\nversion: \"test\"\n");
        Write("catalog/types/probe.yaml", "kind: ResourceType\nname: probe\ndescription: Test-only type.\nclasses: [standard]\nexports: [app]\n");
        Write("catalog/naming.yaml", """
            kind: Naming
            rules:
              law: { pattern: "law-{env}-{hash}", maxLength: 63, allowed: "[a-z0-9-]" }
              cae: { pattern: "cae-{env}-{hash}", maxLength: 32, allowed: "[a-z0-9-]" }
              ca: { pattern: "ca-{env}-{hash}", maxLength: 32, allowed: "[a-z0-9-]" }
            """);
        Write("catalog/mappings/probe.yaml", $$"""
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
              apps:
                template: azure/container-apps-environment
                config:
                  resourceGroupName: ${group.resourceGroupName}
                  location: ${group.location}
                  environmentName: ${name('cae')}
                  workspaceResourceGroupName: ${group.resourceGroupName}
                  workspaceName: ${workspace.workspaceName}
                  workspaceCustomerId: ${workspace.workspaceCustomerId}
              app:
                template: azure/container-app
                config:
                  resourceGroupName: ${group.resourceGroupName}
                  location: ${group.location}
                  containerAppsEnvironmentId: ${apps.environmentId}
                  containerAppName: ${name('ca')}
                  image: {{Image}}
                  targetPort: 8080
                  externalIngress: true
                  cpu: 0.25
                  memory: 0.5Gi
                  minReplicas: 0
                  maxReplicas: 1
                  variables:
                    - { name: GREETING, value: hello }
            exports:
              app: ${app.containerAppId}
            """);
        foreach (var template in new[] { "resource-group", "log-analytics", "container-apps-environment", "container-app" })
        {
            var from = Path.Combine(AppContext.BaseDirectory, "catalog", "templates", "azure", template, "Pulumi.yaml");
            Write($"catalog/templates/azure/{template}/Pulumi.yaml", File.ReadAllText(from));
        }

        var environment = Write("environment.yaml", $"kind: Environment\nname: s15a-{_suffix}\nregion: {region}\ntier: team\nvalues:\n  probeGroup: {ResourceGroup}\n");
        var workload = Write("workload.yaml", "apiVersion: just-deliver/v1\nkind: Workload\nmetadata: { name: s15a-probe, team: platform-team }\ncontainer:\n  image: example.invalid/probe:1\nrequires:\n  - type: probe\n");

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
        var apps = first.IndexOf("s15a-probe/s15a-" + _suffix + "/probe/apps: deployed", StringComparison.Ordinal);
        var app = first.IndexOf("s15a-probe/s15a-" + _suffix + "/probe/app: deployed", StringComparison.Ordinal);
        Assert.True(apps >= 0 && app > apps, "environment then app, both deployed:\n" + first);

        var content = File.ReadAllText(Path.Combine(catalog, "templates", "azure", "container-app", "Pulumi.yaml"));
        var outputs = await backend.GetOutputsAsync($"s15a-probe.s15a-{_suffix}.probe.app", content, CancellationToken.None);
        Assert.NotNull(outputs);
        var appId = outputs["containerAppId"].Value!;
        Assert.Contains($"/resourceGroups/{ResourceGroup}/", appId, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(outputs["principalId"].Value));
        Assert.False(string.IsNullOrWhiteSpace(outputs["latestRevisionName"].Value));

        Assert.Equal("Multiple", Show(appId, "properties.configuration.activeRevisionsMode"));
        Assert.Equal("0", Show(appId, "properties.template.scale.minReplicas"));
        Assert.Equal("1", Show(appId, "properties.template.scale.maxReplicas"));
        Assert.Equal("SystemAssigned", Show(appId, "identity.type"));
        Assert.Equal(outputs["principalId"].Value, Show(appId, "identity.principalId"));
        Assert.Equal("hello", Show(appId, "properties.template.containers[0].env[?name=='GREETING'].value | [0]"));
        Assert.Equal("0.25", Show(appId, "properties.template.containers[0].resources.cpu"));
        Assert.Equal("0.5Gi", Show(appId, "properties.template.containers[0].resources.memory"));

        var (secondCode, second) = await DeployAsync(backend, workload, environment, catalog);
        Assert.True(secondCode == 0, second);
        foreach (var node in new[] { "group", "workspace", "apps", "app" })
        {
            Assert.Contains($"probe/{node}: unchanged (no changes", second);
        }
    }

    private static async Task<(int Code, string Output)> DeployAsync(IBackEndProvider backend, string workload, string environment, string catalog)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await Cli.RunAsync(["deploy", workload, "--env", environment, "--catalog", catalog], stdout, stderr, CancellationToken.None, backend);
        return (code, stdout + stderr.ToString());
    }
}
