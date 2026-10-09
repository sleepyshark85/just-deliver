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

    public Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken) =>
        InWorkDirectoryAsync(package.StackName, async workDir =>
        {
            var (stack, changes, onEvent) = await PrepareStackAsync(package, workDir, cancellationToken);

            var stackUpResult = await stack.UpAsync(new UpOptions
            {
                Logger = _logger,
                OnEvent = onEvent,
            }, cancellationToken);

            return new DeploymentResult()
            {
                Outputs = ToEntries(stackUpResult.Outputs),
                Summary = stackUpResult.Summary.ResourceChanges?.ToDictionary(
                    kvp => kvp.Key.ToString(),
                    kvp => kvp.Value
                ) ?? new Dictionary<string, int>(),
                Changes = changes,
            };
        });

    public Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken) =>
        InWorkDirectoryAsync(package.StackName, async workDir =>
        {
            var (stack, changes, onEvent) = await PrepareStackAsync(package, workDir, cancellationToken);

            var previewResult = await stack.PreviewAsync(new PreviewOptions
            {
                OnEvent = onEvent,
            }, cancellationToken);

            return new DeploymentResult()
            {
                Outputs = new Dictionary<string, ConfigEntry>(),
                Summary = previewResult.ChangeSummary?.ToDictionary(
                    kvp => kvp.Key.ToString(),
                    kvp => kvp.Value
                ) ?? new Dictionary<string, int>(),
                Changes = changes,
            };
        });

    public Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string project, string stackName, CancellationToken cancellationToken) =>
        InWorkDirectoryAsync(stackName, async workDir =>
        {
            // The backend scopes stacks by project, so the workspace needs a project file; it holds no resources.
            await File.WriteAllTextAsync(Path.Combine(workDir, "Pulumi.yaml"), $"name: {project}\nruntime: yaml\n", cancellationToken);

            var pulumiStack = StackNames.ToPulumi(stackName);
            var workspace = await LocalWorkspace.CreateAsync(new LocalWorkspaceOptions
            {
                WorkDir = workDir,
                EnvironmentVariables = _optionsMonitor.CurrentValue.GetEnvironmentVariables(),
            }, cancellationToken);

            var stacks = await workspace.ListStacksAsync(cancellationToken);
            if (!stacks.Any(s => s.Name == pulumiStack))
            {
                return null;
            }

            var stack = await WorkspaceStack.SelectAsync(pulumiStack, workspace, cancellationToken);
            return ToEntries(await stack.GetOutputsAsync(cancellationToken));
        });

    private Task<T> InWorkDirectoryAsync<T>(string graphStack, Func<string, Task<T>> operation) =>
        WorkDirectory.RunAsync(_optionsMonitor.CurrentValue.ScratchDirectory, StackNames.ToPulumi(graphStack), operation);

    private static Dictionary<string, ConfigEntry> ToEntries(IEnumerable<KeyValuePair<string, OutputValue>> outputs) =>
        outputs.ToDictionary(kvp => kvp.Key, kvp => new ConfigEntry(FormatOutputValue(kvp.Value.Value), kvp.Value.IsSecret));

    private async Task<(WorkspaceStack Stack, List<ResourceChange> Changes, Action<EngineEvent> OnEvent)> PrepareStackAsync(
        DeploymentPackage package, string workDir, CancellationToken cancellationToken)
    {
        var stackName = StackNames.ToPulumi(package.StackName);

        await File.WriteAllTextAsync(Path.Combine(workDir, "Pulumi.yaml"), package.DeploymentContent, cancellationToken);

        if (!string.IsNullOrEmpty(package.DeploymentDefaultParametersContent))
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, $"Pulumi.{stackName}.yaml"), package.DeploymentDefaultParametersContent, cancellationToken);
        }

        var stack = await LocalWorkspace.CreateOrSelectStackAsync(new LocalProgramArgs(stackName, workDir)
        {
            EnvironmentVariables = _optionsMonitor.CurrentValue.GetEnvironmentVariables(),
        }, cancellationToken);

        await stack.SetAllConfigAsync(package.DeploymentParameters.ToDictionary(
            kvp => kvp.Key,
            kvp => new ConfigValue(
                kvp.Value.Value ?? throw new ArgumentException($"Parameter '{kvp.Key}' has no value.", nameof(package)),
                kvp.Value.IsSecret)), cancellationToken);

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
        }, cancellationToken);

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
