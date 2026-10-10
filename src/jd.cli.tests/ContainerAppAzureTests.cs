using jd.core.bp;
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
public sealed class ContainerAppAzureTests : ProbeAzureTest
{
    private const string Image = "ghcr.io/sleepyshark85/just-deliver-sample-app@sha256:267b1385d6102da0d5693506e740d509a40b654e6daa2a99c1169938d182cab9";

    protected override string Tag => "s15a";

    // The value of a JMESPath query on the resource, as tsv.
    private string Show(string resourceId, string query)
    {
        var result = Az.Run("resource", "show", "--ids", resourceId, "--query", query, "-o", "tsv");
        return result.Code == 0 ? result.Output.Trim() : throw new InvalidOperationException($"az resource show {resourceId} failed: {result.Error}");
    }

    // The test catalog; the revision suffix and the traffic (flow YAML) are what the tests vary between deploys.
    private void WriteCatalog(string revisionSuffix, string traffic)
    {
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
                  maxInactiveRevisions: 5
                  revisionSuffix: {{revisionSuffix}}
                  traffic: {{traffic}}
                  variables:
                    - { name: GREETING, value: hello }
            exports:
              app: ${app.containerAppId}
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
        CopyTemplates("resource-group", "log-analytics", "container-apps-environment", "container-app");
    }

    [Fact]
    public async Task Deploy_creates_a_multi_revision_app_scaled_to_zero_with_an_identity_and_the_variable_and_a_second_deploy_changes_nothing()
    {
        var (backend, workload, environment, catalog) = Prepare();
        WriteCatalog("a", LatestRevisionTraffic);

        var (firstCode, first) = await DeployAsync(backend, workload, environment, catalog);
        Assert.True(firstCode == 0, first);
        var apps = first.IndexOf("s15a-probe/s15a-" + Suffix + "/probe/apps: deployed", StringComparison.Ordinal);
        var app = first.IndexOf("s15a-probe/s15a-" + Suffix + "/probe/app: deployed", StringComparison.Ordinal);
        Assert.True(apps >= 0 && app > apps, "environment then app, both deployed:\n" + first);

        var outputs = await AppOutputsAsync(backend, catalog);
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

    [Fact]
    public async Task A_dark_revision_gets_no_traffic_and_the_shift_moves_all_of_it_to_the_new_revision_without_creating_one()
    {
        var (backend, workload, environment, catalog) = Prepare();

        WriteCatalog("a", LatestRevisionTraffic);
        var (liveCode, live) = await DeployAsync(backend, workload, environment, catalog);
        Assert.True(liveCode == 0, live);
        var appId = (await AppOutputsAsync(backend, catalog))["containerAppId"].Value!;
        var appName = appId[(appId.LastIndexOf('/') + 1)..];
        string a = $"{appName}--a", b = $"{appName}--b";
        var revisions = Revisions(appId);
        Assert.Equal([a], revisions.Keys);
        Assert.Equal(100, revisions[a].Weight);

        // Dark: revision b is created and provisioned, and revision a keeps all the traffic.
        WriteCatalog("b", $"[{{ revisionName: {a}, weight: 100 }}]");
        var (darkCode, dark) = await DeployAsync(backend, workload, environment, catalog);
        Assert.True(darkCode == 0, dark);
        revisions = Revisions(appId);
        Assert.Equal([a, b], revisions.Keys.Order());
        Assert.Equal("Provisioned", revisions[b].State);
        Assert.Equal((100, 0), (revisions[a].Weight, revisions[b].Weight));

        // Shift: only the traffic changes, so all of it moves to b and no revision is created.
        WriteCatalog("b", $"[{{ revisionName: {b}, weight: 100 }}]");
        var (shiftCode, shift) = await DeployAsync(backend, workload, environment, catalog);
        Assert.True(shiftCode == 0, shift);
        revisions = Revisions(appId);
        Assert.Equal([a, b], revisions.Keys.Order());
        Assert.Equal((0, 100), (revisions[a].Weight, revisions[b].Weight));
    }

    private const string LatestRevisionTraffic = "[{ latestRevision: true, weight: 100 }]";

    // The backend, workload, environment and catalog directory of one probe; the catalog files are written by WriteCatalog.
    private (IBackEndProvider Backend, string Workload, string Environment, string Catalog) Prepare()
    {
        var environment = Write("environment.yaml", $"kind: Environment\nname: s15a-{Suffix}\nregion: {Region}\ntier: team\nvalues:\n  probeGroup: {ResourceGroup}\n");
        var workload = Write("workload.yaml", "apiVersion: just-deliver/v1\nkind: Workload\nmetadata: { name: s15a-probe, team: platform-team }\ncontainer:\n  image: example.invalid/probe:1\nrequires:\n  - type: probe\n");
        return (CreateBackend(), workload, environment, Path.Combine(Root, "catalog"));
    }

    private async Task<Dictionary<string, ConfigEntry>> AppOutputsAsync(IBackEndProvider backend, string catalog)
    {
        var content = File.ReadAllText(Path.Combine(catalog, "templates", "azure", "container-app", "Pulumi.yaml"));
        return await backend.GetOutputsAsync($"s15a-probe.s15a-{Suffix}.probe.app", content, CancellationToken.None)
            ?? throw new InvalidOperationException("the app node has no outputs");
    }

    // The revisions of the app by name with their provisioning state and traffic weight (a revision without traffic reports none).
    private Dictionary<string, (string State, int Weight)> Revisions(string appId)
    {
        var result = Az.Run("rest", "--method", "get", "--url", $"https://management.azure.com{appId}/revisions?api-version=2024-03-01",
            "--query", "value[].{name:name,state:properties.provisioningState,weight:properties.trafficWeight}", "-o", "json");
        if (result.Code != 0)
        {
            throw new InvalidOperationException($"az rest revisions of {appId} failed: {result.Error}");
        }

        return System.Text.Json.JsonDocument.Parse(result.Output).RootElement.EnumerateArray().ToDictionary(
            revision => revision.GetProperty("name").GetString()!,
            revision => (revision.GetProperty("state").GetString()!, revision.GetProperty("weight") is { ValueKind: System.Text.Json.JsonValueKind.Number } weight ? weight.GetInt32() : 0));
    }
}
