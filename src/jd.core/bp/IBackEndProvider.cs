namespace jd.core.bp;

public interface IBackEndProvider
{
    Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken);

    /// <summary>Dry run: validates and computes changes without creating/modifying resources.</summary>
    Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken);

    /// <summary>
    /// The outputs of an already-deployed stack, read from state without refresh or preview (cheap).
    /// Null when the stack has not been deployed (it does not exist, or only a preview created it). Takes the same
    /// package as <see cref="DeployAsync"/> so the stack is looked up in the same project; its parameters are not used.
    /// </summary>
    Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(DeploymentPackage package, CancellationToken cancellationToken);
}
