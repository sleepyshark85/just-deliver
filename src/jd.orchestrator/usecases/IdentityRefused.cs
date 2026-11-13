namespace jd.orchestrator.usecases;

/// <summary>The principal the run authenticates as cannot be named: the credential is missing, malformed or refused.</summary>
/// <param name="Reason">The identity's own words.</param>
public sealed record IdentityRefused(string Reason) : Problem;
