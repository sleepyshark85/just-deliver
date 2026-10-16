namespace jd.core.identity;

/// <summary>The principal a run authenticates with, as the identity provider reports it (not as any file or environment claims).</summary>
/// <param name="TenantId">The directory the principal belongs to.</param>
/// <param name="ClientId">The principal's application (client) id.</param>
/// <param name="ObjectId">The principal's object id in the directory.</param>
public sealed record DeployerIdentity(string TenantId, string ClientId, string ObjectId);

/// <summary>
/// Names the principal the deploy runs as. The only input is the credential the process holds: no definition, catalog, descriptor,
/// release set or stack output reaches it, so workload data cannot say who deployed.
/// </summary>
public interface IDeployerIdentity
{
    Task<DeployerIdentity> GetAsync(CancellationToken cancellationToken);
}
