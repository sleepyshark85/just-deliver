using System.Buffers.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using jd.core.identity;

namespace jd.azure;

/// <summary>
/// The deploying principal as Microsoft Entra reports it: the claims of an Azure Resource Manager access token the credential obtains in
/// this call. The token comes straight from Entra over TLS, so its claims are read without validating the signature: they say what Entra
/// says the credential is, whatever the environment claims.
/// </summary>
public sealed class ArmDeployerIdentity(TokenCredential credential) : IDeployerIdentity
{
    // The token is requested for Azure Resource Manager, the API every deploy goes through: its resource's default scope. A named protocol
    // constant inside an Azure adapter (standards section 1); the MVP is public cloud only, other clouds need configuration (E53).
    private const string ArmScope = "https://management.azure.com/.default";

    private const string TenantClaim = "tid";
    private const string ClientClaim = "appid";
    private const string ObjectClaim = "oid";

    /// <summary>Throws <see cref="InvalidOperationException"/>, fit to show a person, when Entra refuses the credential or the token cannot be read.</summary>
    public async Task<DeployerIdentity> GetAsync(CancellationToken cancellationToken)
    {
        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), cancellationToken);
        }
        catch (AuthenticationFailedException ex)
        {
            // The SDK's message is a page of protocol detail; the person needs to know which settings to check. Nothing of the credential is echoed.
            throw new InvalidOperationException(
                $"Azure did not accept the credential in {ArmCredentials.TenantVariable}, {ArmCredentials.ClientVariable} and {ArmCredentials.SecretVariable}, or could not be reached; check them (is the secret correct and unexpired?) and the network.", ex);
        }

        using var payload = Payload(token.Token);
        return new DeployerIdentity(Claim(payload, TenantClaim), Claim(payload, ClientClaim), Claim(payload, ObjectClaim));
    }

    private static JsonDocument Payload(string jwt)
    {
        var parts = jwt.Split('.');
        try
        {
            return parts.Length == 3 ? JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])) : throw new FormatException("expected three dot-separated parts");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new InvalidOperationException($"the access token is not a readable JWT: {ex.Message}", ex);
        }
    }

    private static string Claim(JsonDocument payload, string name) =>
        payload.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(name, out var value) && value is { ValueKind: JsonValueKind.String } && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidOperationException($"the access token has no '{name}' claim, so the deploying principal cannot be named.");
}
