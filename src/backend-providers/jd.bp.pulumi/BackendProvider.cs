using jd.core.bp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pulumi.Automation;
using System.Linq;

namespace jd.bp.pulumi;

internal class BackendProvider : IBackEndProvider
{
    private readonly IOptionsMonitor<PulumiBackendOptions> _optionsMonitor;
    private readonly ILogger<BackendProvider> _logger;

    public BackendProvider(IOptionsMonitor<PulumiBackendOptions> optionsMonitor, ILogger<BackendProvider> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async Task<DeploymentResult> DeployAsync(DeploymentPackage package)
    {
        var workDir = Path.Combine(_optionsMonitor.CurrentValue.ScratchDireisctory, "just-deliver", package.Name, package.Version);
        Directory.CreateDirectory(workDir);

        await File.WriteAllTextAsync(Path.Combine(workDir, "Pulumi.yaml"), package.DeploymentContent);

        var stack = await LocalWorkspace.CreateOrSelectStackAsync(new LocalProgramArgs(package.Name, workDir)
        {
            EnvironmentVariables = _optionsMonitor.CurrentValue.GetEnvironmentVariables(),
        });

        await stack.SetAllConfigAsync(package.DeploymentParameters.ToDictionary(
            kvp => kvp.Key,
            kvp => new ConfigValue(kvp.Value.Value, kvp.Value.IsSecret)));

        await stack.RefreshAsync(new RefreshOptions
        { 
            //Use OnEvent instead
            OnStandardOutput = message => _logger.LogInformation(message),
            OnStandardError = error => _logger.LogError(error)
        });
        var stackUpResult = await stack.UpAsync(new UpOptions { Logger = _logger });

        return new DeploymentResult()
        {
            Outputs = stackUpResult.Outputs.ToDictionary(
                kvp => kvp.Key,
                kvp => new ConfigEntry(kvp.Value.Value.ToString(), kvp.Value.IsSecret)
            )
        };
    }
}
