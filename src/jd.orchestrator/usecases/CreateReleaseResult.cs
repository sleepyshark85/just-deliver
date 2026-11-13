using jd.resolver;
using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="CreateRelease"/> built.</summary>
/// <param name="Errors">Every problem in the manifest and the definitions, each at its document and location; empty when the set was built.</param>
/// <param name="Outcome"><see cref="Outcome.Succeeded"/> when the set was built, else <see cref="Outcome.Failed"/>.</param>
/// <param name="Set">The release set; set exactly when there are no errors.</param>
/// <param name="Document">The set's document, as UTF-8 without a byte-order mark; set exactly when <paramref name="Set"/> is.</param>
public sealed record CreateReleaseResult(IReadOnlyList<LoadError> Errors, Outcome Outcome, ReleaseSet? Set, ReadOnlyMemory<byte>? Document);
