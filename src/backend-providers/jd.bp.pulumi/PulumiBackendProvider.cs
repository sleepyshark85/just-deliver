using System.Text.Json;
using jd.core.bp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pulumi.Automation;
using Pulumi.Automation.Events;

namespace jd.bp.pulumi;

internal class PulumiBackendProvider : IBackEndProvider
{
    private readonly IOptionsMonitor<PulumiBackendOptions> _optionsMonitor;
    private readonly ILogger<PulumiBackendProvider> _logger;
    private readonly IResourceChangeParser<StepEventMetadata> _resourceChangeParser;

    public PulumiBackendProvider(
        IOptionsMonitor<PulumiBackendOptions> optionsMonitor,
        ILogger<PulumiBackendProvider> logger,
        IResourceChangeParser<StepEventMetadata> resourceChangeParser)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _resourceChangeParser = resourceChangeParser;
    }

    public async Task<DeploymentResult> DeployAsync(DeploymentPackage package)
    {
        var (stack, changes, onEvent) = await PrepareStackAsync(package);

        var stackUpResult = await stack.UpAsync(new UpOptions
        {
            Logger = _logger,
            OnEvent = onEvent,
        });

        return new DeploymentResult()
        {
            Outputs = stackUpResult.Outputs.ToDictionary(
                kvp => kvp.Key,
                kvp => new ConfigEntry(FormatOutputValue(kvp.Value.Value), kvp.Value.IsSecret)
            ),
            Summary = stackUpResult.Summary.ResourceChanges?.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value
            ) ?? new Dictionary<string, int>(),
            Changes = changes,
        };
    }

    public async Task<DeploymentResult> PreviewAsync(DeploymentPackage package)
    {
        var (stack, changes, onEvent) = await PrepareStackAsync(package);

        var previewResult = await stack.PreviewAsync(new PreviewOptions
        {
            OnEvent = onEvent,
        });

        return new DeploymentResult()
        {
            Outputs = new Dictionary<string, ConfigEntry>(),
            Summary = previewResult.ChangeSummary?.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value
            ) ?? new Dictionary<string, int>(),
            Changes = changes,
        };
    }

    private async Task<(WorkspaceStack Stack, List<ResourceChange> Changes, Action<EngineEvent> OnEvent)> PrepareStackAsync(DeploymentPackage package)
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
            kvp => new ConfigValue(
                kvp.Value.Value ?? throw new ArgumentException($"Parameter '{kvp.Key}' has no value.", nameof(package)),
                kvp.Value.IsSecret)));

        var changes = new List<ResourceChange>();
        void OnEngineEvent(EngineEvent engineEvent)
        {
            var metadata = engineEvent.ResourcePreEvent?.Metadata;
            var change = metadata is null ? null : _resourceChangeParser.Parse(metadata);
            if (change is not null)
            {
                changes.Add(change);
            }
        }

        await stack.RefreshAsync(new RefreshOptions
        {
            OnEvent = OnEngineEvent,
            OnStandardError = error => _logger.LogError(error),
        });

        return (stack, changes, OnEngineEvent);
    }

    // Stack outputs are typed as object because a Pulumi program can export any
    // JSON-serializable shape, so scalars, collections, and null all need handling.
    private static string? FormatOutputValue(object? value) => value switch
    {
        null => null,
        string text => text,
        System.Collections.IEnumerable => JsonSerializer.Serialize(value),
        _ => value.ToString(),
    };
}
