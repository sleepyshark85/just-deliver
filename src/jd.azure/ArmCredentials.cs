using Azure.Core;
using Azure.Identity;

namespace jd.azure;

/// <summary>
/// The service principal the deploy authenticates with: the <c>ARM_*</c> variables Pulumi's Azure provider reads too, so the principal
/// named here is the one the stacks are deployed as. The composition root reads them and passes the values in.
/// </summary>
public sealed record ArmCredentials(string? TenantId, string? ClientId, string? ClientSecret)
{
    public const string TenantVariable = "ARM_TENANT_ID";
    public const string ClientVariable = "ARM_CLIENT_ID";
    public const string SecretVariable = "ARM_CLIENT_SECRET";

    /// <summary>Names the variables that are not set, never any value; null when all are set.</summary>
    public string? Problem
    {
        get
        {
            var missing = new[] { (TenantVariable, TenantId), (ClientVariable, ClientId), (SecretVariable, ClientSecret) }
                .Where(v => string.IsNullOrEmpty(v.Item2)).Select(v => v.Item1).ToList();
            return missing.Count == 0
                ? null
                : $"the deploying principal is the service principal in the environment, and {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not set.";
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> with <see cref="Problem"/> when a variable is missing.</summary>
    public TokenCredential ToCredential() =>
        Problem is { } problem ? throw new InvalidOperationException(problem) : new ClientSecretCredential(TenantId, ClientId, ClientSecret);
}
