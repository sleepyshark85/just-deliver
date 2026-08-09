namespace jd.core.bp;

public interface IBackEndProvider
{
    Task<DeploymentResult> DeployAsync(DeploymentPackage package);
}
