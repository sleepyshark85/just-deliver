using Pulumi;
using Pulumi.Automation;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;

const string projectName = "provisioner";
const string stackName = "dev";
var workDir = Directory.GetCurrentDirectory();

// Inline Pulumi program: provisions a resource group + a Free tier (F1) App Service.
// Config values come straight from Pulumi.dev.yaml, read natively by the Pulumi
// engine via Config - no custom parsing.
static Task<IDictionary<string, object?>> PulumiProgram()
{
    var config = new Config();
    var resourceGroupName = config.Require("resourceGroupName");
    var appName = config.Require("appName");

    var resourceGroup = new ResourceGroup(resourceGroupName, new ResourceGroupArgs
    {
        ResourceGroupName = resourceGroupName,
    });

    var appServicePlan = new AppServicePlan($"{appName}-plan", new AppServicePlanArgs
    {
        ResourceGroupName = resourceGroup.Name,
        Kind = "App",
        Sku = new SkuDescriptionArgs
        {
            Name = "F1",
            Tier = "Free",
        },
    });

    var webApp = new WebApp(appName, new WebAppArgs
    {
        Name = appName,
        ResourceGroupName = resourceGroup.Name,
        ServerFarmId = appServicePlan.Id,
    });

    return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>
    {
        ["resourceGroupName"] = resourceGroup.Name,
        ["resourceGroupId"] = resourceGroup.Id,
        ["appServicePlanId"] = appServicePlan.Id,
        ["webAppDefaultHostName"] = webApp.DefaultHostName,
    });
}

var stackArgs = new InlineProgramArgs(projectName, stackName, PulumiFn.Create(PulumiProgram))
{
    // Points at the folder containing Pulumi.yaml + Pulumi.dev.yaml so the Pulumi
    // CLI reads project/stack settings and config directly from those files.
    WorkDir = workDir,
    EnvironmentVariables = new Dictionary<string, string?>
    {
        // Local backend still encrypts state; passphrase is required non-interactively.
        // Use an empty passphrase for this local sample/testing only.
        ["PULUMI_CONFIG_PASSPHRASE"] = ""
    }
};

var stack = await LocalWorkspace.CreateOrSelectStackAsync(stackArgs);

Console.WriteLine("Refreshing stack...");
await stack.RefreshAsync(new RefreshOptions { OnStandardOutput = Console.WriteLine });

Console.WriteLine("Running pulumi up...");
var result = await stack.UpAsync(new UpOptions { OnStandardOutput = Console.WriteLine });

Console.WriteLine($"Update summary: {result.Summary.Result}");
foreach (var (key, value) in result.Outputs)
{
    Console.WriteLine($"  {key}: {value.Value}");
}
