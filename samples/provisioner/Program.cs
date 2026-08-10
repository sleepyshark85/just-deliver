using jd.bp.pulumi;
using jd.core.bp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Linq;

var baseDir = Directory.GetCurrentDirectory();

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole());
services.RegisterPulumiBackend(options =>
{
    options.BackendUrl = "file://~";
    // Local backend still encrypts state; passphrase is required non-interactively.
    // Use an empty passphrase for this local sample/testing only.
    options.ConfigPassPhrase = "";
});

var provider = services.BuildServiceProvider();
var backendProvider = provider.GetRequiredService<IBackEndProvider>();

// --- Step 1: provision the resource group ---
Console.WriteLine("=== resource-group: deploy ===");

var resourceGroupContent = await File.ReadAllTextAsync(Path.Combine(baseDir, "resource-group", "Pulumi.yaml"));
var resourceGroupDefaultParametersContent = await ReadIfExistsAsync(Path.Combine(baseDir, "resource-group", "Pulumi.default.yaml"));

var resourceGroupResult = await backendProvider.DeployAsync(new DeploymentPackage
{
    Name = "resource-group",
    Version = "dev",
    DeploymentContent = resourceGroupContent,
    DeploymentDefaultParametersContent = resourceGroupDefaultParametersContent,
    DeploymentParameters = new Dictionary<string, ConfigEntry>
    {
        ["resourceGroupName"] = new ConfigEntry("rg-just-deliver"),
        ["location"] = new ConfigEntry("southeastasia"),
    },
});

var resourceGroupName = resourceGroupResult.Outputs["resourceGroupName"].Value
    ?? throw new InvalidOperationException("resource-group stack did not export resourceGroupName");

Console.WriteLine($"Resource group provisioned: {resourceGroupName}");

// --- Step 2: feed that output into app-service's config, then provision it ---
Console.WriteLine();
Console.WriteLine("=== app-service: deploy ===");

var appServiceContent = await File.ReadAllTextAsync(Path.Combine(baseDir, "app-service", "Pulumi.yaml"));
var appServiceDefaultParametersContent = await ReadIfExistsAsync(Path.Combine(baseDir, "app-service", "Pulumi.default.yaml"));

var appServiceResult = await backendProvider.DeployAsync(new DeploymentPackage
{
    Name = "app-service",
    Version = "dev",
    DeploymentContent = appServiceContent,
    DeploymentDefaultParametersContent = appServiceDefaultParametersContent,
    // Only parameters not already covered by Pulumi.default.yaml.
    DeploymentParameters = new Dictionary<string, ConfigEntry>
    {
        ["resourceGroupName"] = new ConfigEntry(resourceGroupName),
        ["location"] = new ConfigEntry("southeastasia"),
        ["planName"] = new ConfigEntry("just-deliver-sample-app-plan"),
        ["appName"] = new ConfigEntry("just-deliver-sample-app"),
    },
});

// --- Step 3: provision the Log Analytics workspace backing Azure Monitor ---
Console.WriteLine();
Console.WriteLine("=== azure-monitor: deploy ===");

var azureMonitorContent = await File.ReadAllTextAsync(Path.Combine(baseDir, "azure-monitor", "Pulumi.yaml"));
var azureMonitorDefaultParametersContent = await ReadIfExistsAsync(Path.Combine(baseDir, "azure-monitor", "Pulumi.default.yaml"));

var azureMonitorResult = await backendProvider.DeployAsync(new DeploymentPackage
{
    Name = "azure-monitor",
    Version = "dev",
    DeploymentContent = azureMonitorContent,
    DeploymentDefaultParametersContent = azureMonitorDefaultParametersContent,
    DeploymentParameters = new Dictionary<string, ConfigEntry>
    {
        ["resourceGroupName"] = new ConfigEntry(resourceGroupName),
        ["location"] = new ConfigEntry("southeastasia"),
        ["workspaceName"] = new ConfigEntry("just-deliver-sample-logs"),
    },
});

var workspaceResourceId = azureMonitorResult.Outputs["workspaceId"].Value
    ?? throw new InvalidOperationException("azure-monitor stack did not export workspaceId");

// --- Step 4: provision Application Insights, tied to that workspace ---
Console.WriteLine();
Console.WriteLine("=== application-insights: deploy ===");

var appInsightsContent = await File.ReadAllTextAsync(Path.Combine(baseDir, "application-insights", "Pulumi.yaml"));
var appInsightsDefaultParametersContent = await ReadIfExistsAsync(Path.Combine(baseDir, "application-insights", "Pulumi.default.yaml"));

var appInsightsResult = await backendProvider.DeployAsync(new DeploymentPackage
{
    Name = "application-insights",
    Version = "dev",
    DeploymentContent = appInsightsContent,
    DeploymentDefaultParametersContent = appInsightsDefaultParametersContent,
    DeploymentParameters = new Dictionary<string, ConfigEntry>
    {
        ["resourceGroupName"] = new ConfigEntry(resourceGroupName),
        ["location"] = new ConfigEntry("southeastasia"),
        ["appInsightsName"] = new ConfigEntry("just-deliver-sample-appinsights"),
        ["workspaceResourceId"] = new ConfigEntry(workspaceResourceId),
    },
});

// --- Step 5: provision the Cosmos DB account, database, and container ---
Console.WriteLine();
Console.WriteLine("=== cosmos-db: deploy ===");

var cosmosDbContent = await File.ReadAllTextAsync(Path.Combine(baseDir, "cosmos-db", "Pulumi.yaml"));
var cosmosDbDefaultParametersContent = await ReadIfExistsAsync(Path.Combine(baseDir, "cosmos-db", "Pulumi.default.yaml"));

var cosmosDbResult = await backendProvider.DeployAsync(new DeploymentPackage
{
    Name = "cosmos-db",
    Version = "dev",
    DeploymentContent = cosmosDbContent,
    DeploymentDefaultParametersContent = cosmosDbDefaultParametersContent,
    DeploymentParameters = new Dictionary<string, ConfigEntry>
    {
        ["resourceGroupName"] = new ConfigEntry(resourceGroupName),
        ["location"] = new ConfigEntry("southeastasia"),
        ["accountName"] = new ConfigEntry("just-deliver-sample-cosmos"),
    },
});

Console.WriteLine();
Console.WriteLine("=== Outputs ===");
PrintOutputs("resource-group", resourceGroupResult);
PrintOutputs("app-service", appServiceResult);
PrintOutputs("azure-monitor", azureMonitorResult);
PrintOutputs("application-insights", appInsightsResult);
PrintOutputs("cosmos-db", cosmosDbResult);

Console.WriteLine();
Console.WriteLine("=== Changes ===");
PrintChanges("resource-group", resourceGroupResult);
PrintChanges("app-service", appServiceResult);
PrintChanges("azure-monitor", azureMonitorResult);
PrintChanges("application-insights", appInsightsResult);
PrintChanges("cosmos-db", cosmosDbResult);

static async Task<string?> ReadIfExistsAsync(string path) =>
    File.Exists(path) ? await File.ReadAllTextAsync(path) : null;

static void PrintOutputs(string name, DeploymentResult result)
{
    Console.WriteLine($"{name}:");
    foreach (var (key, value) in result.Outputs)
    {
        Console.WriteLine($"  {key}: {value.Value}");
    }
}

static void PrintChanges(string name, DeploymentResult result)
{
    Console.WriteLine($"{name}:");
    Console.WriteLine($"  summary: {string.Join(", ", result.Summary.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
    foreach (var change in result.Changes)
    {
        Console.WriteLine($"  {change.Operation} {change.Type} ({change.Urn})");
        foreach (var property in change.ChangedProperties)
        {
            Console.WriteLine($"    {property.Path}: {property.OldValue ?? "<none>"} -> {property.NewValue ?? "<none>"}");
        }
    }
}
