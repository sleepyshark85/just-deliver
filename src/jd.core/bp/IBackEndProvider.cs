namespace jd.core.bp;

public interface IBackEndProvider
{
    Task<DeploymentResult> DeployAsync(DeploymentPackage package);

    /// <summary>Dry run: validates and computes changes without creating/modifying resources.</summary>
    Task<DeploymentResult> PreviewAsync(DeploymentPackage package);
}
