namespace jd.orchestrator.usecases;

/// <summary>A promotion passed its checks and its walk starts: the set kept for the label on the source environment is deployed to the target.</summary>
/// <param name="Label">The release label.</param>
/// <param name="From">The environment the set is kept on: the policy's <c>qualifiedOn</c>.</param>
/// <param name="SetSha256">The hash of the set the checks judged.</param>
public sealed record Promoting(string Label, string From, string SetSha256) : Progress;
