using System.Buffers.Text;
using System.Text;
using Azure.Core;
using Azure.Identity;
using jd.core.identity;
using Xunit;

namespace jd.azure.tests;

// The adapter is driven by a credential that returns a hand-built token, so no network or Azure identity is involved.
public sealed class ArmDeployerIdentityTests
{
    private sealed class FixedCredential(string token) : TokenCredential
    {
        public List<string[]> Requests { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => GetTokenAsync(requestContext, cancellationToken).GetAwaiter().GetResult();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests.Add(requestContext.Scopes);
            return new ValueTask<AccessToken>(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private static string Token(string payloadJson) =>
        $"{Encode("""{"alg":"RS256","typ":"JWT"}""")}.{Encode(payloadJson)}.signature";

    private static string Encode(string text) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));

    private static Task<DeployerIdentity> IdentityOf(string token, FixedCredential? credential = null) =>
        new ArmDeployerIdentity(credential ?? new FixedCredential(token)).GetAsync(CancellationToken.None);

    [Fact]
    public async Task The_identity_is_read_from_the_claims_of_the_token()
    {
        var credential = new FixedCredential(Token("""{"tid":"tenant-1","appid":"client-1","oid":"object-1","sub":"other","azp":"someone-else"}"""));

        var identity = await IdentityOf(string.Empty, credential);

        Assert.Equal(new DeployerIdentity("tenant-1", "client-1", "object-1"), identity);
        Assert.Single(credential.Requests);
        Assert.Single(credential.Requests[0]);
        Assert.EndsWith("/.default", credential.Requests[0][0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"appid":"c","oid":"o"}""", "tid")]
    [InlineData("""{"tid":"t","oid":"o"}""", "appid")]
    [InlineData("""{"tid":"t","appid":"c"}""", "oid")]
    [InlineData("""{"tid":"t","appid":"c","oid":""}""", "oid")]
    [InlineData("""{"tid":"t","appid":"c","oid":7}""", "oid")]
    public async Task A_token_without_a_claim_is_refused_naming_the_claim(string payload, string claim)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => IdentityOf(Token(payload)));

        Assert.Contains($"'{claim}'", error.Message, StringComparison.Ordinal);
    }

    private sealed class RefusingCredential(string message) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new AuthenticationFailedException(message);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromException<AccessToken>(new AuthenticationFailedException(message));
    }

    [Fact]
    public async Task A_credential_Entra_refuses_is_a_clear_error_naming_the_variables_without_the_sdk_text_or_any_secret()
    {
        var sdkMessage = "ClientSecretCredential authentication failed: AADSTS7000215: Invalid client secret provided. the-secret";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ArmDeployerIdentity(new RefusingCredential(sdkMessage)).GetAsync(CancellationToken.None));

        Assert.Contains("ARM_TENANT_ID, ARM_CLIENT_ID and ARM_CLIENT_SECRET", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AADSTS", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("the-secret", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("header.!!!.signature")]
    [InlineData("header.bm90LWpzb24.signature")] // "not-json"
    public async Task A_token_that_is_not_a_jwt_is_refused_with_a_clear_error(string token)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => IdentityOf(token));

        Assert.Contains("not a readable JWT", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payload_that_is_not_an_object_is_refused()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => IdentityOf(Token("""["tid"]""")));

        Assert.Contains("'tid'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "client", "the-secret", "ARM_TENANT_ID is not set")]
    [InlineData("tenant", null, "the-secret", "ARM_CLIENT_ID is not set")]
    [InlineData("tenant", "client", null, "ARM_CLIENT_SECRET is not set")]
    [InlineData("", "", "the-secret", "ARM_TENANT_ID, ARM_CLIENT_ID are not set")]
    public void Missing_variables_are_named_and_the_secret_is_never_echoed(string? tenant, string? client, string? secret, string expected)
    {
        var credentials = new ArmCredentials(tenant, client, secret);

        Assert.Contains(expected, credentials.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("the-secret", credentials.Problem, StringComparison.Ordinal);
        var error = Assert.Throws<InvalidOperationException>(credentials.ToCredential);
        Assert.Equal(credentials.Problem, error.Message);
    }

    [Fact]
    public void The_credentials_never_print_the_secret()
    {
        var credentials = new ArmCredentials("tenant", "client", "the-secret");

        Assert.DoesNotContain("the-secret", credentials.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void All_three_variables_set_give_a_credential_without_a_problem()
    {
        var credentials = new ArmCredentials("tenant", "client", "secret");

        Assert.Null(credentials.Problem);
        Assert.NotNull(credentials.ToCredential());
    }
}
