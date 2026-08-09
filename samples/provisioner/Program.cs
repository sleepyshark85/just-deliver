using jd.bp.pulumi;
using jd.core.bp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

var resourceGroupResult = await backendProvider.DeployAsync(new DeploymentPackage
{
    Name = "resource-group",
    Version = "dev",
    DeploymentContent = resourceGroupContent,
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

var appServiceResult = await backendProvider.DeployAsync(new DeploymentPackage
{
    Name = "app-service",
    Version = "dev",
    DeploymentContent = appServiceContent,
    DeploymentParameters = new Dictionary<string, ConfigEntry>
    {
        ["resourceGroupName"] = new ConfigEntry(resourceGroupName),
        ["location"] = new ConfigEntry("southeastasia"),

        // App Service Plan
        ["planName"] = new ConfigEntry("just-deliver-sample-app-plan"),
        ["skuName"] = new ConfigEntry("F1"),
        ["skuTier"] = new ConfigEntry("Free"),
        ["skuSize"] = new ConfigEntry("F1"),
        ["skuFamily"] = new ConfigEntry("F"),
        ["skuCapacity"] = new ConfigEntry("1"),
        ["planKind"] = new ConfigEntry("App"),
        ["reserved"] = new ConfigEntry("false"),

        // Web App - name must be globally unique across Azure
        ["appName"] = new ConfigEntry("just-deliver-sample-app"),
        ["webAppKind"] = new ConfigEntry("app"),
        ["httpsOnly"] = new ConfigEntry("true"),
        ["alwaysOn"] = new ConfigEntry("false"),
        ["http20Enabled"] = new ConfigEntry("true"),
        ["ftpsState"] = new ConfigEntry("Disabled"),
        ["netFrameworkVersion"] = new ConfigEntry("v4.0"),
        ["use32BitWorkerProcess"] = new ConfigEntry("true"),
    },
});

Console.WriteLine();
Console.WriteLine("=== Outputs ===");
Console.WriteLine("resource-group:");
foreach (var (key, value) in resourceGroupResult.Outputs)
{
    Console.WriteLine($"  {key}: {value.Value}");
}

Console.WriteLine("app-service:");
foreach (var (key, value) in appServiceResult.Outputs)
{
    Console.WriteLine($"  {key}: {value.Value}");
}
