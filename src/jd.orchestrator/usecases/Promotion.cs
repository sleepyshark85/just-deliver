using jd.core.identity;

namespace jd.orchestrator.usecases;

/// <summary>What a promotion settled before its walk (docs/architecture/approvals.md section 5); only <see cref="Promote"/> builds one.</summary>
/// <param name="Principal">Who runs it, named already.</param>
/// <param name="SetSha256">The hash of the set the promotion judged.</param>
internal sealed record Promotion(DeployerIdentity Principal, string SetSha256);
