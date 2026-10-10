using Xunit;

namespace jd.cli.tests;

/// <summary>
/// Real resources, free tier only: a test-only catalog whose one type maps to a resource group and a Log Analytics workspace
/// (daily cap 0.15 GB, the template's default 30-day retention) that references the group's outputs. Deploys it with the real
/// Pulumi backend on a local file state, deploys again for zero changes, and always deletes the resource group.
/// Run with <c>tools/verify.sh --azure</c>; needs the sandbox team identity (<c>ARM_CLIENT_ID</c>, <c>ARM_CLIENT_SECRET</c>, <c>ARM_TENANT_ID</c>, <c>ARM_SUBSCRIPTION_ID</c>) in the environment, <c>JD_REGION</c> and the pulumi CLI.
/// </summary>
[Trait("Category", "Azure")]
public sealed class DeployAzureTests : ProbeAzureTest
{
    protected override string Tag => "s11";

    [Fact]
    public async Task Deploy_creates_a_group_and_a_workspace_in_it_and_a_second_deploy_changes_nothing()
    {
        var catalog = Path.Combine(Root, "catalog");
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
        WriteRuntimeStandIn();
        CopyTemplates("resource-group", "log-analytics");

        var environment = Write("environment.yaml", $"kind: Environment\nname: s11-{Suffix}\nregion: {Region}\ntier: team\nvalues:\n  probeGroup: {ResourceGroup}\n");
        var workload = Write("workload.yaml", "apiVersion: just-deliver/v1\nkind: Workload\nmetadata: { name: s11-probe, team: platform-team }\ncontainer:\n  image: example.invalid/probe:1\nrequires:\n  - type: probe\n");

        var backend = CreateBackend();

        var (firstCode, first) = await DeployAsync(backend, workload, environment, catalog);
        Assert.True(firstCode == 0, first);
        var group = first.IndexOf("s11-probe/s11-" + Suffix + "/probe/group: deployed", StringComparison.Ordinal);
        var workspace = first.IndexOf("s11-probe/s11-" + Suffix + "/probe/workspace: deployed", StringComparison.Ordinal);
        Assert.True(group >= 0 && workspace > group, "group then workspace, both deployed:\n" + first);

        // The workspace was created in the group by name, so the group's output reached the workspace node.
        var workspaceStack = $"s11-probe.s11-{Suffix}.probe.workspace";
        var content = File.ReadAllText(Path.Combine(catalog, "templates", "azure", "log-analytics", "Pulumi.yaml"));
        var outputs = await backend.GetOutputsAsync(workspaceStack, content, CancellationToken.None);
        Assert.NotNull(outputs);
        Assert.Contains($"/resourceGroups/{ResourceGroup}/", outputs["workspaceId"].Value, StringComparison.OrdinalIgnoreCase);

        var (secondCode, second) = await DeployAsync(backend, workload, environment, catalog);
        Assert.True(secondCode == 0, second);
        Assert.Contains("probe/group: unchanged (no changes", second);
        Assert.Contains("probe/workspace: unchanged (no changes", second);
    }
}
