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

    public Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(DeploymentPackage package, CancellationToken cancellationToken) =>
        InWorkDirectoryAsync(package.StackName, async workDir =>
        {
            // The backend scopes stacks by project; the template content gives the same project as DeployAsync.
            await File.WriteAllTextAsync(Path.Combine(workDir, "Pulumi.yaml"), package.DeploymentContent, cancellationToken);

            var pulumiStack = StackNames.ToPulumi(package.StackName);
            var workspace = await LocalWorkspace.CreateAsync(new LocalWorkspaceOptions
            {
                WorkDir = workDir,
                EnvironmentVariables = _optionsMonitor.CurrentValue.GetEnvironmentVariables(),
            }, cancellationToken);

            var stacks = await workspace.ListStacksAsync(cancellationToken);
            // A preview creates an empty stack; with no resources recorded it has never been deployed.
            if (!stacks.Any(s => s.Name == pulumiStack && s.ResourceCount > 0))
            {
                return null;
            }

            var stack = await WorkspaceStack.SelectAsync(pulumiStack, workspace, cancellationToken);
            return ToEntries(await stack.GetOutputsAsync(cancellationToken));
        });

    // Async so an invalid stack name fails the returned task instead of throwing at the call.
    private async Task<T> InWorkDirectoryAsync<T>(string graphStack, Func<string, Task<T>> operation) =>
        await WorkDirectory.RunAsync(_optionsMonitor.CurrentValue.ScratchDirectory, StackNames.ToPulumi(graphStack), operation);

    private static Dictionary<string, ConfigEntry> ToEntries(IEnumerable<KeyValuePair<string, OutputValue>> outputs) =>
        outputs.ToDictionary(kvp => kvp.Key, kvp => new ConfigEntry(FormatOutputValue(kvp.Value.Value), kvp.Value.IsSecret));

    private async Task<(WorkspaceStack Stack, List<ResourceChange> Changes, Action<EngineEvent> OnEvent)> PrepareStackAsync(
        DeploymentPackage package, string workDir, CancellationToken cancellationToken)
    {
        var stackName = StackNames.ToPulumi(package.StackName);

        await File.WriteAllTextAsync(Path.Combine(workDir, "Pulumi.yaml"), package.DeploymentContent, cancellationToken);

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

        // Refresh events are not recorded as changes: they would report an operation on every resource.
        await stack.RefreshAsync(new RefreshOptions
        {
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
