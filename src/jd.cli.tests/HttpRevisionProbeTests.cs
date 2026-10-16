using System.Net;
using jd.orchestrator;
using jd.resolver.catalog;
using Xunit;

namespace jd.cli.tests;

// The HTTP probe against a stubbed handler: no network.
public sealed class HttpRevisionProbeTests
{
    private static readonly Probe Spec = new("/health", 200, 60, 1);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            return respond(request, cancellationToken);
        }
    }

    private static StubHandler Answering(HttpStatusCode status) => new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    private static HttpRevisionProbe ProbeOver(StubHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task It_passes_on_the_expected_status_and_asks_https_fqdn_then_path()
    {
        var handler = Answering(HttpStatusCode.OK);

        var result = await ProbeOver(handler).ProbeAsync("app--r1.example.test", Spec, CancellationToken.None);

        Assert.Equal(new ProbeResult(true, "HTTP 200"), result);
        Assert.Equal(new Uri("https://app--r1.example.test/health"), Assert.Single(handler.Requests));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.NoContent, 204)]
    public async Task It_fails_on_any_other_status_and_says_which(HttpStatusCode status, int code)
    {
        var result = await ProbeOver(Answering(status)).ProbeAsync("app.example.test", Spec, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal($"HTTP {code}", result.Detail);
    }

    [Fact]
    public async Task The_clients_the_cli_uses_does_not_follow_a_redirect_so_the_revisions_own_302_fails_the_probe()
    {
        // A local listener (no network): /health redirects to /elsewhere, which would answer 200 if the client followed.
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                var redirect = context.Request.Url?.AbsolutePath == "/health";
                context.Response.StatusCode = redirect ? 302 : 200;
                if (redirect)
                {
                    context.Response.RedirectLocation = $"http://127.0.0.1:{port}/elsewhere";
                }

                context.Response.Close();
            }
        });
        using var client = HttpRevisionProbe.CreateClient();

        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health"));
        listener.Stop();

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => serving).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    [Fact]
    public async Task A_302_is_a_failed_probe_with_its_status()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)));

        var result = await ProbeOver(handler).ProbeAsync("app.example.test", Spec, CancellationToken.None);

        Assert.Equal(new ProbeResult(false, "HTTP 302"), result);
    }

    [Fact]
    public async Task It_fails_with_the_error_message_when_the_connection_fails()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("Name or service not known"));

        var result = await ProbeOver(handler).ProbeAsync("app.example.test", Spec, CancellationToken.None);

        Assert.Equal(new ProbeResult(false, "Name or service not known"), result);
    }

    [Fact]
    public async Task An_attempt_that_hangs_is_cut_off_after_the_probes_interval()
    {
        var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await ProbeOver(handler).ProbeAsync("app.example.test", Spec, CancellationToken.None);

        Assert.Equal(new ProbeResult(false, "no answer within 1s"), result);
    }

    [Fact]
    public async Task Cancelling_the_run_is_not_a_failed_probe()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProbeOver(handler).ProbeAsync("app.example.test", Spec, cancelled.Token));
    }
}
