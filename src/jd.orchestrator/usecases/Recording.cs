using jd.core.identity;
using jd.core.records;

namespace jd.orchestrator.usecases;

/// <summary>What a recorded run has before anything is deployed: where its records go, who deploys, the kept set's hash, and its IQ.</summary>
/// <param name="Records">The records the set is kept beside.</param>
/// <param name="Identity">The principal the run deploys as.</param>
/// <param name="SetSha256">The kept set's hash.</param>
/// <param name="Qualifier">Checks the deployment (IQ).</param>
public sealed record Recording(IRecordStore Records, DeployerIdentity Identity, string SetSha256, Qualifier Qualifier);
