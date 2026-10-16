using jd.resolver.catalog;

namespace jd.orchestrator;

/// <summary>One probe attempt's verdict; <c>Detail</c> is what the probe saw: the status it got, or why it could not ask.</summary>
public sealed record ProbeResult(bool Passed, string Detail);

/// <summary>
/// Asks a deployed revision once whether it is healthy. The orchestrator owns the retries; the adapter owns the transport. The target
/// is only the revision's <c>fqdn</c> and the mapping's <see cref="Probe.Path"/>.
/// </summary>
public interface IRevisionProbe
{
    Task<ProbeResult> ProbeAsync(string fqdn, Probe probe, CancellationToken cancellationToken);
}
