using System.Buffers.Text;
using System.Text.Json;
using Azure.Core;
using jd.orchestrator;

namespace jd.azure;

/// <summary>
/// The deploying principal as Microsoft Entra reports it: the claims of an Azure Resource Manager access token the credential obtains in
/// this call. The token comes straight from Entra over TLS, so its claims are read without validating the signature: they say what Entra
/// says the credential is, whatever the environment claims.
/// </summary>
public sealed class ArmDeployerIdentity(TokenCredential credential) : IDeployerIdentity
{
    // The token is requested for Azure Resource Manager, the API every deploy goes through: its resource's default scope.
    private const string ArmScope = "https://management.azure.com/.default";

    private const string TenantClaim = "tid";
    private const string ClientClaim = "appid";
    private const string ObjectClaim = "oid";

    public async Task<DeployerIdentity> GetAsync(CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), cancellationToken);
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
