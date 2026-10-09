namespace jd.core.bp;

public interface IBackEndProvider
{
    Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken);

    /// <summary>Dry run: validates and computes changes without creating/modifying resources.</summary>
    Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken);

    /// <summary>
    /// The outputs of an already-deployed stack, read from state without refresh or preview (cheap).
    /// Null when the stack does not exist ("not deployed"). <paramref name="project"/> is the template name
    /// (<see cref="DeploymentPackage.Name"/>), <paramref name="stackName"/> the graph's stack.
    /// </summary>
    Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string project, string stackName, CancellationToken cancellationToken);
}
