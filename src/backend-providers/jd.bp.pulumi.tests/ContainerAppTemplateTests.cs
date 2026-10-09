using System.Text.Json;
using Pulumi.Automation;
using Xunit;

namespace jd.bp.pulumi.tests;

/// <summary>
/// The <c>container-app</c> template through the real Pulumi CLI and the real azure-native provider, previewed on a local file
/// backend in a temp directory. The provider validates the inputs, so a template the Container App would reject fails here; a
/// preview of a create calls no Azure API, so the credentials are placeholders and nothing is created. Reads the resource's inputs
/// from the engine events, which the backend provider does not expose. Fails (does not skip) without the CLI.
/// </summary>
[Trait("Category", "Pulumi")]
public sealed class ContainerAppTemplateTests : IDisposable
{
    private const string Placeholder = "00000000-0000-0000-0000-000000000000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jd-pulumi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // The inputs of the one Container App the preview would create, as JSON.
    private async Task<JsonElement> PreviewInputsAsync(string variables)
    {
        var work = Path.Combine(_root, "work");
        var state = Path.Combine(_root, "state");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(work, "Pulumi.yaml"),
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "catalog", "templates", "azure", "container-app", "Pulumi.yaml")));

        var stack = await LocalWorkspace.CreateOrSelectStackAsync(new LocalProgramArgs("preview", work)
        {
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["PULUMI_BACKEND_URL"] = "file://" + state,
                ["PULUMI_CONFIG_PASSPHRASE"] = "test-passphrase",
            },
        });
        await stack.SetAllConfigAsync(new Dictionary<string, ConfigValue>
        {
            ["azure-native:clientId"] = new(Placeholder),
            ["azure-native:clientSecret"] = new("placeholder", isSecret: true),
            ["azure-native:tenantId"] = new(Placeholder),
            ["azure-native:subscriptionId"] = new(Placeholder),
            ["resourceGroupName"] = new("rg"),
            ["location"] = new("westeurope"),
            ["containerAppsEnvironmentId"] = new($"/subscriptions/{Placeholder}/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/env"),
            ["containerAppName"] = new("orders"),
            ["image"] = new("registry.example/orders@sha256:abc"),
            ["targetPort"] = new("8080"),
            ["externalIngress"] = new("true"),
            ["cpu"] = new("0.25"),
            ["memory"] = new("0.5Gi"),
            ["minReplicas"] = new("0"),
            ["maxReplicas"] = new("1"),
            ["variables"] = new(variables),
        });

        object? inputs = null;
        await stack.PreviewAsync(new PreviewOptions
        {
            OnEvent = e =>
            {
                if (e.ResourcePreEvent?.Metadata is { Type: "azure-native:app:ContainerApp" } metadata)
                {
                    inputs = metadata.New?.Inputs;
                }
            },
        });

        return JsonSerializer.SerializeToElement(inputs ?? throw new InvalidOperationException("the preview did not plan a Container App"));
    }

    [Fact]
    public async Task The_preview_creates_a_multi_revision_app_with_a_system_identity_scale_resources_and_the_env_list()
    {
        var inputs = await PreviewInputsAsync("""[{"name":"A","value":"1"},{"name":"B","value":"x,\"q\""}]""");

        Assert.Equal("Multiple", inputs.GetProperty("configuration").GetProperty("activeRevisionsMode").GetString());
        Assert.Equal("SystemAssigned", inputs.GetProperty("identity").GetProperty("type").GetString());
        var ingress = inputs.GetProperty("configuration").GetProperty("ingress");
        Assert.Equal(8080, ingress.GetProperty("targetPort").GetInt32());
        Assert.True(ingress.GetProperty("external").GetBoolean());
        var traffic = Assert.Single(ingress.GetProperty("traffic").EnumerateArray());
        Assert.True(traffic.GetProperty("latestRevision").GetBoolean());
        Assert.Equal(100, traffic.GetProperty("weight").GetInt32());
        Assert.False(inputs.TryGetProperty("workloadProfileName", out _));

        var template = inputs.GetProperty("template");
        var container = Assert.Single(template.GetProperty("containers").EnumerateArray());
        Assert.Equal("registry.example/orders@sha256:abc", container.GetProperty("image").GetString());
        Assert.Equal(0.25, container.GetProperty("resources").GetProperty("cpu").GetDouble());
        Assert.Equal("0.5Gi", container.GetProperty("resources").GetProperty("memory").GetString());
        var env = container.GetProperty("env").EnumerateArray().Select(v => (v.GetProperty("name").GetString(), v.GetProperty("value").GetString())).ToList();
        Assert.Equal([("A", "1"), ("B", "x,\"q\"")], env);
        Assert.Equal(0, template.GetProperty("scale").GetProperty("minReplicas").GetInt32());
        Assert.Equal(1, template.GetProperty("scale").GetProperty("maxReplicas").GetInt32());
    }

    [Fact]
    public async Task No_variables_gives_an_empty_env_list()
    {
        var inputs = await PreviewInputsAsync("[]");

        var container = Assert.Single(inputs.GetProperty("template").GetProperty("containers").EnumerateArray());
        Assert.Empty(container.GetProperty("env").EnumerateArray());
    }
}
