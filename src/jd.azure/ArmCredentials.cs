using Azure.Core;
using Azure.Identity;

namespace jd.azure;

/// <summary>
/// The principal the deploy authenticates with: the service principal in the <c>ARM_*</c> variables when all are set, the Azure CLI sign-in when
/// none is. Pulumi's Azure provider makes the same choice, so the principal named here is the one the stacks are deployed as. The composition
/// root reads the variables and passes the values in. A class, not a record: a record's generated <c>ToString</c> would print the secret.
/// </summary>
public sealed class ArmCredentials(string? tenantId, string? clientId, string? clientSecret)
{
    public const string TenantVariable = "ARM_TENANT_ID";
    public const string ClientVariable = "ARM_CLIENT_ID";
    public const string SecretVariable = "ARM_CLIENT_SECRET";

    /// <summary>What to check when Azure refuses the credential, for either kind.</summary>
    public const string CheckHint = $"check {TenantVariable}, {ClientVariable} and {SecretVariable} when they are set (is the secret correct and unexpired?), else the Azure CLI sign-in (az login)";

    /// <summary>
    /// The Azure CLI sign-in when no variable is set, else the service principal. Throws <see cref="InvalidOperationException"/> naming the variables
    /// that are not set when only some are (never any value), or with a clear message when the SDK refuses a malformed value.
    /// </summary>
    public TokenCredential ToCredential()
    {
        var missing = new[] { (TenantVariable, tenantId), (ClientVariable, clientId), (SecretVariable, clientSecret) }
            .Where(v => string.IsNullOrEmpty(v.Item2)).Select(v => v.Item1).ToArray();
        if (missing.Length == 3)
        {
            return new AzureCliCredential();
        }

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"the deploying principal is the service principal in the environment, and {string.Join(", ", missing)} {(missing.Length == 1 ? "is" : "are")} not set; set all three, or none to use the Azure CLI sign-in (az login).");
        }

        try
        {
            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }
        catch (ArgumentException)
        {
            // The SDK refuses a malformed value (a tenant id that is neither a directory id nor a domain name); its message is not repeated.
            throw new InvalidOperationException($"{TenantVariable}, {ClientVariable} and {SecretVariable} do not make a valid credential; check that {TenantVariable} is a directory id or domain name.");
        }
    }
}
