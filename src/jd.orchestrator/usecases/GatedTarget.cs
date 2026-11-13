using jd.core.identity;
using jd.resolver.environment;

namespace jd.orchestrator.usecases;

/// <summary>The gated environment an approval or a promotion is for, and who runs it (docs/architecture/approvals.md sections 4 and 5).</summary>
/// <param name="Environment">The environment's descriptor.</param>
/// <param name="Policy">Its approval policy.</param>
/// <param name="Principal">The principal of the credential.</param>
internal sealed record GatedTarget(EnvironmentDescriptor Environment, ApprovalPolicy Policy, DeployerIdentity Principal);
