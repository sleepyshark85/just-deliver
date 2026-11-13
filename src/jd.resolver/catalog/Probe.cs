namespace jd.resolver.catalog;

/// <summary>
/// How the platform checks a deployed runtime is healthy: GET <see cref="Path"/> must answer <see cref="ExpectedStatus"/>, tried every
/// <see cref="IntervalSeconds"/> for at most <see cref="TimeoutSeconds"/>. Data only; the release flow executes it.
/// </summary>
public sealed record Probe(string Path, int ExpectedStatus, int TimeoutSeconds, int IntervalSeconds);
