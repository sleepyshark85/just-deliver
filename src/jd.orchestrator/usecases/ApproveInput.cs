using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>What <see cref="Approve"/> approves.</summary>
/// <param name="Label">The release label whose kept set is approved.</param>
/// <param name="Environment">The gated environment's descriptor.</param>
/// <param name="Meaning">What the approver asserts by approving: recorded verbatim, never interpreted. Empty when none was given.</param>
public sealed record ApproveInput(string Label, SourceDocument Environment, string Meaning);
