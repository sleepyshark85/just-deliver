using jd.resolver;
using jd.resolver.environment;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="EnvUp"/> did.</summary>
/// <param name="Errors">Every problem found before the walk, or in composing the descriptor after it, each at its document and location.</param>
/// <param name="Outcome"><see cref="Outcome.Succeeded"/> when the descriptor was composed, else <see cref="Outcome.Failed"/>.</param>
/// <param name="Run">The walk, its last node the failed one when it failed; null when the run stopped before it.</param>
/// <param name="Environment">The environment the walk provisioned (the definition over the base); null when the run stopped before it.</param>
/// <param name="Descriptor">The environment's descriptor, as UTF-8 without a byte-order mark; set exactly when the run succeeded.</param>
public sealed record EnvUpResult(IReadOnlyList<LoadError> Errors, Outcome Outcome, RunReport? Run, EnvironmentDescriptor? Environment, ReadOnlyMemory<byte>? Descriptor);
