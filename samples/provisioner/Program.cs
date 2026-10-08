using jd.bp.pulumi;
using jd.core.bp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var baseDir = Directory.GetCurrentDirectory();
var preview = args.Contains("--preview");
// The Web App always gets a managed identity (inert on its own); granting it access
// to Cosmos DB / App Insights is the opt-in part.
var wireManagedIdentity = args.Contains("--wire-managed-identity");

// Despite the name, this built-in role covers publishing all telemetry types to
// Application Insights, not just metrics.
const string MonitoringMetricsPublisherRoleId = "3913510d-42f4-4e42-8a64-420c390055eb";

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
var resourceGroupResult = await RunStackAsync("resource-group", new Dictionary<string, ConfigEntry>
{
    ["resourceGroupName"] = new ConfigEntry("rg-just-deliver"),
    ["location"] = new ConfigEntry("southeastasia"),
});
var resourceGroupName = GetChainedValue(resourceGroupResult, "resourceGroupName", "rg-just-deliver");

// --- Step 2: feed that output into app-service's config, then provision it ---
var appServiceResult = await RunStackAsync("app-service", new Dictionary<string, ConfigEntry>
{
    ["resourceGroupName"] = new ConfigEntry(resourceGroupName),
    ["location"] = new ConfigEntry("southeastasia"),
    ["planName"] = new ConfigEntry("just-deliver-sample-app-plan"),
    ["appName"] = new ConfigEntry("just-deliver-sample-app"),
});

// --- Step 3: provision the Log Analytics workspace backing Azure Monitor ---
var azureMonitorResult = await RunStackAsync("azure-monitor", new Dictionary<string, ConfigEntry>
{
    ["resourceGroupName"] = new ConfigEntry(resourceGroupName),
    ["location"] = new ConfigEntry("southeastasia"),
    ["workspaceName"] = new ConfigEntry("just-deliver-sample-logs"),
});
var workspaceResourceId = GetChainedValue(azureMonitorResult, "workspaceId", "<unknown-until-deployed>");

// --- Step 4: provision Application Insights, tied to that workspace ---
var appInsightsResult = await RunStackAsync("application-insights", new Dictionary<string, ConfigEntry>
{
    ["resourceGroupName"] = new ConfigEntry(resourceGroupName),
    ["location"] = new ConfigEntry("southeastasia"),
    ["appInsightsName"] = new ConfigEntry("just-deliver-sample-appinsights"),
    ["workspaceResourceId"] = new ConfigEntry(workspaceResourceId),
    ["disableLocalAuth"] = new ConfigEntry(wireManagedIdentity ? "true" : "false"),
});

// --- Step 5: provision the Cosmos DB account, database, and container ---
var cosmosDbResult = await RunStackAsync("cosmos-db", new Dictionary<string, ConfigEntry>
{
    ["resourceGroupName"] = new ConfigEntry(resourceGroupName),
    ["location"] = new ConfigEntry("southeastasia"),
    ["accountName"] = new ConfigEntry("just-deliver-sample-cosmos"),
});

// --- Steps 6 and 7 (optional): grant the Web App's managed identity data access ---
DeploymentResult? cosmosDbAccessResult = null;
DeploymentResult? appInsightsAccessResult = null;
if (wireManagedIdentity)
{
    var appServicePrincipalId = GetChainedValue(appServiceResult, "webAppPrincipalId", "<unknown-until-deployed>");

    // Cosmos DB SQL API has its own data-plane role system, separate from Azure RBAC.
    var cosmosAccountId = GetChainedValue(cosmosDbResult, "accountId", "<unknown-until-deployed>");
    var cosmosAccountName = GetChainedValue(cosmosDbResult, "accountName", "just-deliver-sample-cosmos");

    cosmosDbAccessResult = await RunStackAsync("cosmos-db-access", new Dictionary<string, ConfigEntry>
    {
        ["resourceGroupName"] = new ConfigEntry(resourceGroupName),
        ["accountId"] = new ConfigEntry(cosmosAccountId),
        ["accountName"] = new ConfigEntry(cosmosAccountName),
        ["principalId"] = new ConfigEntry(appServicePrincipalId),
    });

    // Application Insights uses plain Azure RBAC, so it reuses the generic
    // role-assignment definition under its own deployment name.
    var appInsightsId = GetChainedValue(appInsightsResult, "appInsightsId", "<unknown-until-deployed>");

    appInsightsAccessResult = await RunStackAsync("appinsights-metrics-publisher", new Dictionary<string, ConfigEntry>
    {
        ["scope"] = new ConfigEntry(appInsightsId),
        ["roleDefinitionId"] = new ConfigEntry(BuildRoleDefinitionId(appInsightsId, MonitoringMetricsPublisherRoleId)),
        ["principalId"] = new ConfigEntry(appServicePrincipalId),
        ["roleAssignmentId"] = new ConfigEntry("5d1e7c92-4b3a-4f8e-9a2d-1c6b8e4f7a3b"),
    }, definition: "role-assignment");
}

Console.WriteLine();
Console.WriteLine("=== Outputs ===");
PrintOutputs("resource-group", resourceGroupResult);
PrintOutputs("app-service", appServiceResult);
PrintOutputs("azure-monitor", azureMonitorResult);
PrintOutputs("application-insights", appInsightsResult);
PrintOutputs("cosmos-db", cosmosDbResult);
if (cosmosDbAccessResult is not null)
{
    PrintOutputs("cosmos-db-access", cosmosDbAccessResult);
}
if (appInsightsAccessResult is not null)
{
    PrintOutputs("appinsights-metrics-publisher", appInsightsAccessResult);
}

Console.WriteLine();
Console.WriteLine("=== Changes ===");
PrintChanges("resource-group", resourceGroupResult);
PrintChanges("app-service", appServiceResult);
PrintChanges("azure-monitor", azureMonitorResult);
PrintChanges("application-insights", appInsightsResult);
PrintChanges("cosmos-db", cosmosDbResult);
if (cosmosDbAccessResult is not null)
{
    PrintChanges("cosmos-db-access", cosmosDbAccessResult);
}
if (appInsightsAccessResult is not null)
{
    PrintChanges("appinsights-metrics-publisher", appInsightsAccessResult);
}

// `definition` names the folder the Pulumi.yaml comes from; `name` is the deployment
// (stack) name. They differ when a shared definition is deployed more than once -
// each deployment needs its own name so it gets its own state.
async Task<DeploymentResult> RunStackAsync(string name, Dictionary<string, ConfigEntry> parameters, string? definition = null)
{
    var definitionFolder = definition ?? name;

    Console.WriteLine();
    Console.WriteLine($"=== {name}: {(preview ? "preview" : "deploy")} ===");

    var content = await File.ReadAllTextAsync(Path.Combine(baseDir, definitionFolder, "Pulumi.yaml"));
    var defaultParametersContent = await ReadIfExistsAsync(Path.Combine(baseDir, definitionFolder, "Pulumi.default.yaml"));

    var package = new DeploymentPackage
    {
        Name = name,
        Version = "dev",
        DeploymentContent = content,
        DeploymentDefaultParametersContent = defaultParametersContent,
        DeploymentParameters = parameters,
    };

    return preview ? await backendProvider.PreviewAsync(package) : await backendProvider.DeployAsync(package);
}

// Preview doesn't produce real outputs (nothing was actually created), so chained
// stacks fall back to the value that would be passed in instead of reading it back.
string GetChainedValue(DeploymentResult result, string outputKey, string previewFallback) =>
    preview
        ? previewFallback
        : result.Outputs[outputKey].Value ?? throw new InvalidOperationException($"stack did not export {outputKey}");

static async Task<string?> ReadIfExistsAsync(string path) =>
    File.Exists(path) ? await File.ReadAllTextAsync(path) : null;

// Built-in role definitions are addressed by a subscription-scoped path. Every ARM
// resource ID starts with /subscriptions/{id}, so derive it from the target scope
// rather than making the caller supply the subscription separately.
static string BuildRoleDefinitionId(string scope, string roleId)
{
    var segments = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var subscriptionId = segments.Length >= 2 && segments[0] == "subscriptions"
        ? segments[1]
        : "<unknown-subscription>";

    return $"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{roleId}";
}

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
