using jd.core.identity;

namespace jd.orchestrator.usecases;

/// <summary>What a promotion settled before its walk (docs/architecture/approvals.md section 5).</summary>
/// <param name="Principal">Who runs it, named already.</param>
/// <param name="QualifiedOn">The environment the set is kept on: the policy's <c>qualifiedOn</c>.</param>
/// <param name="Label">The set's label.</param>
/// <param name="SetSha256">The hash of the set the promotion judged.</param>
public sealed record Promotion(DeployerIdentity Principal, string QualifiedOn, string Label, string SetSha256);
