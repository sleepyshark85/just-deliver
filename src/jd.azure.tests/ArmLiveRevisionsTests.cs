using System.Net;
using System.Text;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using jd.core.revisions;
using Xunit;

namespace jd.azure.tests;

// The adapter talks to a hand-written Azure Resource Manager through the SDK's HTTP transport, so no network or identity is involved.
public sealed class ArmLiveRevisionsTests
{
    private const string AppId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.App/containerApps/app";

    private sealed class FixedCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }

    private sealed class Answering(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body.Replace("@app", AppId, StringComparison.Ordinal), Encoding.UTF8, "application/json") });
        }
    }

    private static Task<IReadOnlyList<LiveRevision>> RevisionsAsync(Answering handler, string resourceId = AppId)
    {
        var options = new ArmClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        options.Retry.MaxRetries = 0;
        return new ArmLiveRevisions(new FixedCredential(), options).GetAsync(resourceId, CancellationToken.None);
    }

    [Fact]
    public async Task Revisions_come_with_their_name_traffic_weight_and_the_image_of_each_container()
    {
        var handler = new Answering(HttpStatusCode.OK, """
            {"value":[
              {"id":"@app/revisions/app--r1","name":"app--r1","properties":{"trafficWeight":100,"template":{"containers":[{"name":"app","image":"repo@sha256:aaa"},{"name":"side","image":"repo@sha256:bbb"}]}}},
              {"id":"@app/revisions/app--r2","name":"app--r2","properties":{"trafficWeight":0,"template":{"containers":[{"name":"app","image":"repo@sha256:ccc"}]}}}
            ]}
            """);

        var revisions = await RevisionsAsync(handler);

        Assert.Equal(["app--r1", "app--r2"], revisions.Select(r => r.Name));
        Assert.Equal([100, 0], revisions.Select(r => r.TrafficWeight));
        Assert.Equal(["repo@sha256:aaa", "repo@sha256:bbb"], revisions[0].Images);
        Assert.Equal(["repo@sha256:ccc"], revisions[1].Images);
        Assert.Contains("/containerApps/app/revisions", Assert.Single(handler.Requests).AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revision_with_no_reported_weight_holds_none()
    {
        var revisions = await RevisionsAsync(new Answering(HttpStatusCode.OK, """{"value":[{"id":"@app/revisions/app--r1","name":"app--r1","properties":{"template":{"containers":[{"name":"app","image":"i"}]}}}]}"""));

        Assert.Equal(0, Assert.Single(revisions).TrafficWeight);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "ResourceNotFound")]
    [InlineData(HttpStatusCode.Forbidden, "AuthorizationFailed")]
    public async Task A_refusal_names_the_status_and_error_code_and_nothing_else_of_the_reply(HttpStatusCode status, string code)
    {
        var handler = new Answering(status, $$$"""{"error":{"code":"{{{code}}}","message":"secret-looking detail the person does not need"}}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RevisionsAsync(handler));

        Assert.Contains($"HTTP {(int)status}, {code}", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-looking", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resource_id_that_is_not_one_is_refused_clearly()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RevisionsAsync(new Answering(HttpStatusCode.OK, "{}"), "not-an-id"));

        Assert.Contains("not an Azure resource id", error.Message, StringComparison.Ordinal);
    }
}
