namespace jd.resolver.catalog;

/// <summary>
/// How the platform checks a deployed runtime is healthy: GET <see cref="Path"/> over <see cref="Scheme"/> must answer <see cref="ExpectedStatus"/>,
/// tried every <see cref="IntervalSeconds"/> for at most <see cref="TimeoutSeconds"/>. Data only; the release flow executes it.
/// </summary>
public sealed record Probe(string Scheme, string Path, int ExpectedStatus, int TimeoutSeconds, int IntervalSeconds);
