using jd.core.bp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pulumi.Automation;
using Pulumi.Automation.Events;
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

        if (!string.IsNullOrEmpty(package.DeploymentDefaultParametersContent))
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, $"Pulumi.{package.Name}.yaml"), package.DeploymentDefaultParametersContent);
        }

        var stack = await LocalWorkspace.CreateOrSelectStackAsync(new LocalProgramArgs(package.Name, workDir)
        {
            EnvironmentVariables = _optionsMonitor.CurrentValue.GetEnvironmentVariables(),
        });

        await stack.SetAllConfigAsync(package.DeploymentParameters.ToDictionary(
            kvp => kvp.Key,
            kvp => new ConfigValue(kvp.Value.Value, kvp.Value.IsSecret)));

        var changes = new List<ResourceChange>();
        void OnEngineEvent(EngineEvent engineEvent)
        {
            var metadata = engineEvent.ResourcePreEvent?.Metadata;
            if (metadata is null || metadata.Op == OperationType.Same)
            {
                return;
            }

            changes.Add(new ResourceChange
            {
                Urn = metadata.Urn,
                Type = metadata.Type,
                Operation = metadata.Op.ToString(),
                ChangedProperties = metadata.DetailedDiff?.Keys.ToList() ?? new List<string>(),
            });
        }

        await stack.RefreshAsync(new RefreshOptions
        {
            OnEvent = OnEngineEvent,
            OnStandardError = error => _logger.LogError(error),
        });
        var stackUpResult = await stack.UpAsync(new UpOptions
        {
            Logger = _logger,
            OnEvent = OnEngineEvent,
        });

        return new DeploymentResult()
        {
            Outputs = stackUpResult.Outputs.ToDictionary(
                kvp => kvp.Key,
                kvp => new ConfigEntry(kvp.Value.Value.ToString(), kvp.Value.IsSecret)
            ),
            Summary = stackUpResult.Summary.ResourceChanges?.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value
            ) ?? new Dictionary<string, int>(),
            Changes = changes,
        };
    }
}
