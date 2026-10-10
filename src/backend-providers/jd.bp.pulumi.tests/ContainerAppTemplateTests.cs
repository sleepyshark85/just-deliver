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

    private const string LatestTraffic = """[{"latestRevision":true,"weight":100}]""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jd-pulumi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // The inputs of the one Container App the preview would create, as JSON.
    private async Task<JsonElement> PreviewInputsAsync(string variables = "[]", string traffic = LatestTraffic, string revisionSuffix = "a")
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
            ["revisionSuffix"] = new(revisionSuffix),
            ["traffic"] = new(traffic),
            ["maxInactiveRevisions"] = new("5"),
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
        var inputs = await PreviewInputsAsync(variables: """[{"name":"A","value":"1"},{"name":"B","value":"x,\"q\""}]""");

        Assert.Equal("Multiple", inputs.GetProperty("configuration").GetProperty("activeRevisionsMode").GetString());
        Assert.Equal("SystemAssigned", inputs.GetProperty("identity").GetProperty("type").GetString());
        var ingress = inputs.GetProperty("configuration").GetProperty("ingress");
        Assert.Equal(8080, ingress.GetProperty("targetPort").GetInt32());
        Assert.True(ingress.GetProperty("external").GetBoolean());
        Assert.Equal("a", inputs.GetProperty("template").GetProperty("revisionSuffix").GetString());
        Assert.Equal(5, inputs.GetProperty("configuration").GetProperty("maxInactiveRevisions").GetInt32());
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
        var inputs = await PreviewInputsAsync();

        var container = Assert.Single(inputs.GetProperty("template").GetProperty("containers").EnumerateArray());
        Assert.Empty(container.GetProperty("env").EnumerateArray());
    }

    // The traffic items of the planned Container App as (revisionName, latestRevision, weight), the types checked: the weight is an
    // integer and latestRevision a boolean, or the getters throw.
    private static List<(string? Revision, bool? Latest, int Weight)> Traffic(JsonElement inputs) =>
        inputs.GetProperty("configuration").GetProperty("ingress").GetProperty("traffic").EnumerateArray()
            .Select(item => (
                item.TryGetProperty("revisionName", out var name) ? name.GetString() : null,
                item.TryGetProperty("latestRevision", out var latest) ? (bool?)latest.GetBoolean() : null,
                item.GetProperty("weight").GetInt32()))
            .ToList();

    [Fact]
    public async Task A_dark_revision_names_the_new_suffix_and_sends_all_traffic_to_the_previous_revision()
    {
        var inputs = await PreviewInputsAsync(traffic: """[{"revisionName":"orders--a","weight":100}]""", revisionSuffix: "b");

        Assert.Equal("b", inputs.GetProperty("template").GetProperty("revisionSuffix").GetString());
        Assert.Equal([("orders--a", (bool?)null, 100)], Traffic(inputs));
    }

    [Fact]
    public async Task A_shift_sends_all_traffic_to_the_named_new_revision()
    {
        var inputs = await PreviewInputsAsync(traffic: """[{"revisionName":"orders--b","weight":100}]""", revisionSuffix: "b");

        Assert.Equal([("orders--b", (bool?)null, 100)], Traffic(inputs));
    }

    [Fact]
    public async Task Latest_revision_traffic_arrives_as_a_boolean_and_an_integer()
    {
        var inputs = await PreviewInputsAsync();

        Assert.Equal([((string?)null, (bool?)true, 100)], Traffic(inputs));
    }

    [Theory]
    [InlineData("""[{"latestRevision":true,"weight":"100"}]""", "weight")]
    [InlineData("""[{"latestRevision":"true","weight":100}]""", "latestRevision")]
    public async Task Traffic_with_a_text_weight_or_text_latest_revision_is_rejected_by_the_provider(string traffic, string field)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => PreviewInputsAsync(traffic: traffic));

        Assert.Contains(field, error.Message);
    }

    [Fact]
    public async Task The_template_holds_no_traffic_or_revision_opinion()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "catalog", "templates", "azure", "container-app", "Pulumi.yaml"));

        Assert.DoesNotMatch(@"(?m)^\s*-?\s*(latestRevision|revisionName|weight):", text);
        Assert.DoesNotMatch(@"(?m)^\s*revisionSuffix:[ \t]*[^$\s]", text);
    }
}
