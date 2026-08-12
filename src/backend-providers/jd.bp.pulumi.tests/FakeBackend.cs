using jd.bp.pulumi.execution;
using jd.core.bp;

namespace jd.bp.pulumi.tests;

/// <summary>
/// Stands in for Pulumi so the whole execution path is exercised deterministically. Returns the
/// outputs each definition actually declares, so a test failing here means the executor is
/// wrong rather than the cloud being slow.
/// </summary>
public class FakeBackend : IBackEndProvider
{
    private static readonly Dictionary<string, Func<DeploymentPackage, Dictionary<string, string>>> Outputs = new()
    {
        ["resource-group"] = p => new()
        {
            ["resourceGroupName"] = p.DeploymentParameters["resourceGroupName"].Value!,
            ["resourceGroupId"] = $"/subscriptions/{Subscription}/resourceGroups/{p.DeploymentParameters["resourceGroupName"].Value}",
            ["location"] = p.DeploymentParameters["location"].Value!,
        },
        ["cosmos-db"] = p => new()
        {
            ["accountId"] = Arm("Microsoft.DocumentDB/databaseAccounts", p.DeploymentParameters["accountName"].Value!),
            ["accountName"] = p.DeploymentParameters["accountName"].Value!,
            ["documentEndpoint"] = $"https://{p.DeploymentParameters["accountName"].Value}.documents.azure.com:443/",
            ["databaseName"] = p.DeploymentParameters["databaseName"].Value!,
            ["containerName"] = p.DeploymentParameters["containerName"].Value!,
        },
        ["cosmos-db-access"] = _ => new() { ["roleAssignmentId"] = "assignment" },
        ["app-service"] = p => new()
        {
            ["appServicePlanId"] = Arm("Microsoft.Web/serverfarms", p.DeploymentParameters["planName"].Value!),
            ["appServicePlanName"] = p.DeploymentParameters["planName"].Value!,
            ["webAppId"] = Arm("Microsoft.Web/sites", p.DeploymentParameters["appName"].Value!),
            ["webAppName"] = p.DeploymentParameters["appName"].Value!,
            ["webAppDefaultHostName"] = $"{p.DeploymentParameters["appName"].Value}.azurewebsites.net",
            ["webAppPrincipalId"] = PrincipalId,
        },
        ["azure-monitor"] = p => new()
        {
            ["workspaceId"] = Arm("Microsoft.OperationalInsights/workspaces", p.DeploymentParameters["workspaceName"].Value!),
            ["workspaceName"] = p.DeploymentParameters["workspaceName"].Value!,
            ["workspaceCustomerId"] = "customer-id",
        },
        ["application-insights"] = p => new()
        {
            ["appInsightsId"] = Arm("Microsoft.Insights/components", p.DeploymentParameters["appInsightsName"].Value!),
            ["appInsightsName"] = p.DeploymentParameters["appInsightsName"].Value!,
            ["instrumentationKey"] = "key",
            ["connectionString"] = "InstrumentationKey=key;IngestionEndpoint=https://example/",
        },
        ["role-assignment"] = _ => new() { ["roleAssignmentId"] = "assignment" },
    };

    public const string Subscription = "00000000-1111-2222-3333-444444444444";
    public const string PrincipalId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    /// <summary>Stack name -> the parameters it was deployed with, for assertions.</summary>
    public Dictionary<string, Dictionary<string, string?>> Deployed { get; } = new();

    /// <summary>Stack names deployed, in the order they were started.</summary>
    public List<string> Order { get; } = new();

    /// <summary>Definitions that should throw, to exercise fail-fast.</summary>
    public HashSet<string> FailOn { get; } = new();

    /// <summary>Set when a stack was previewed rather than deployed.</summary>
    public List<string> Previewed { get; } = new();

    public Task<DeploymentResult> DeployAsync(DeploymentPackage package) => Run(package, preview: false);

    public Task<DeploymentResult> PreviewAsync(DeploymentPackage package) => Run(package, preview: true);

    private Task<DeploymentResult> Run(DeploymentPackage package, bool preview)
    {
        var definition = DefinitionOf(package);

        lock (Order)
        {
            Order.Add(package.Name);
            Deployed[package.Name] = package.DeploymentParameters.ToDictionary(p => p.Key, p => p.Value.Value);
            if (preview)
            {
                Previewed.Add(package.Name);
            }
        }

        if (FailOn.Contains(definition))
        {
            throw new InvalidOperationException($"{definition} failed on purpose");
        }

        return Task.FromResult(new DeploymentResult
        {
            Outputs = Outputs[definition](package).ToDictionary(o => o.Key, o => new ConfigEntry(o.Value)),
            Summary = new Dictionary<string, int> { ["create"] = 1 },
        });
    }

    /// <summary>The definition is identifiable from its program text, as Pulumi would see it.</summary>
    private static string DefinitionOf(DeploymentPackage package)
    {
        var name = package.DeploymentContent.Split('\n')
            .First(l => l.StartsWith("name:"))["name:".Length..].Trim();

        return name;
    }

    private static string Arm(string provider, string name) =>
        $"/subscriptions/{Subscription}/resourceGroups/rg/providers/{provider}/{name}";
}

/// <summary>Reads definitions straight from the repository, as the real store does.</summary>
public class RepositoryDefinitionStore : IDefinitionStore
{
    private readonly FileSystemDefinitionStore _inner;

    public RepositoryDefinitionStore(string repositoryRoot, string definitionsRoot) =>
        _inner = new FileSystemDefinitionStore(Path.Combine(repositoryRoot, definitionsRoot));

    public Task<string> GetProgramAsync(string definition) => _inner.GetProgramAsync(definition);

    public Task<string?> GetDefaultParametersAsync(string definition) => _inner.GetDefaultParametersAsync(definition);
}
