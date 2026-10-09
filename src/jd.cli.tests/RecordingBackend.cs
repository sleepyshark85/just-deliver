using jd.core.bp;

namespace jd.cli.tests;

// A fake backend: records each stack it is asked to deploy or preview, and fails the one named in FailOn.
internal sealed class RecordingBackend : IBackEndProvider
{
    public List<string> Calls { get; } = [];
    public string? FailOn { get; set; }

    public Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken)
    {
        Calls.Add("deploy " + package.StackName);
        return package.StackName == FailOn
            ? throw new InvalidOperationException("provider exploded")
            : Task.FromResult(new DeploymentResult { Outputs = [], Summary = new() { ["Create"] = 1 }, Changes = [new ResourceChange { Urn = "urn", Type = "azure-native:t:T", Operation = "create" }] });
    }

    public Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken)
    {
        Calls.Add("preview " + package.StackName);
        return Task.FromResult(new DeploymentResult { Outputs = [], Summary = new() { ["Create"] = 1 } });
    }

    public Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string stackName, string deploymentContent, CancellationToken cancellationToken) =>
        Task.FromResult<Dictionary<string, ConfigEntry>?>(null);
}
