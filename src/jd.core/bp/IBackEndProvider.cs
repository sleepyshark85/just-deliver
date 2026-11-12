namespace jd.core.bp;

/// <summary>The port to the deployment engine: deploys, previews and reads one backend stack per graph node.</summary>
public interface IBackEndProvider
{
    /// <summary>Brings the package's stack to its template and parameters, creating or changing resources, and returns its outputs.</summary>
    Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken);

    /// <summary>Dry run: validates and computes changes without creating/modifying resources.</summary>
    Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken);

    /// <summary>
    /// The outputs of an already-deployed stack, read from state without refresh or preview (cheap).
    /// Null when the stack has not been deployed (it does not exist, or only a preview created it). Takes the stack
    /// name and template content <see cref="DeployAsync"/> was given, so the stack is looked up in the same project.
    /// </summary>
    Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string stackName, string deploymentContent, CancellationToken cancellationToken);
}
