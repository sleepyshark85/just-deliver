namespace jd.core.identity;

/// <summary>The principal a run authenticates with, as the identity provider reports it (not as any file or environment claims).</summary>
/// <param name="Directory">Where the principal lives: an Entra tenant, a cloud project.</param>
/// <param name="Principal">The principal's stable key, the only field any rule compares (ADR 0022).</param>
/// <param name="Name">The principal's name, for people.</param>
public sealed record DeployerIdentity(string Directory, string Principal, string Name);

/// <summary>
/// Names the principal the deploy runs as. The only input is the credential the process holds: no definition, catalog, descriptor,
/// release set or stack output reaches it, so workload data cannot say who deployed.
/// </summary>
public interface IDeployerIdentity
{
    /// <summary>Asks the identity provider who the process's credential is; throws when the credential cannot authenticate.</summary>
    Task<DeployerIdentity> GetAsync(CancellationToken cancellationToken);
}
