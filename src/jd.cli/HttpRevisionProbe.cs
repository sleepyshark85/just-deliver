using System.Globalization;
using jd.orchestrator;
using jd.resolver.catalog;

namespace jd.cli;

/// <summary>
/// The probe over HTTPS: <c>GET https://&lt;fqdn&gt;&lt;path&gt;</c> passes when the status is the expected one. An attempt is cut off after
/// the probe's interval, so a revision that hangs costs one interval, not the client's default timeout.
/// </summary>
public sealed class HttpRevisionProbe(HttpClient client) : IRevisionProbe
{
    /// <summary>The client the CLI uses. It does not follow redirects: the probe judges the revision's own answer, not another host's.</summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    public async Task<ProbeResult> ProbeAsync(string fqdn, Probe probe, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(probe.IntervalSeconds));
        try
        {
            using var response = await client.GetAsync(new Uri($"https://{fqdn}{probe.Path}"), attempt.Token);
            return new ProbeResult((int)response.StatusCode == probe.ExpectedStatus, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException)
        {
            return new ProbeResult(false, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(false, $"no answer within {probe.IntervalSeconds.ToString(CultureInfo.InvariantCulture)}s");
        }
    }
}
