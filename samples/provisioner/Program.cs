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

Console.WriteLine();
Console.WriteLine("=== Changes ===");
PrintChanges("resource-group", resourceGroupResult);
PrintChanges("app-service", appServiceResult);

static async Task<string?> ReadIfExistsAsync(string path) =>
    File.Exists(path) ? await File.ReadAllTextAsync(path) : null;

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
