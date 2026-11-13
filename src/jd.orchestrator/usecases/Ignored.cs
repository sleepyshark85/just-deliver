namespace jd.orchestrator.usecases;

/// <summary>A reader skipped an approval file that cannot count: it is unreadable or invalid, or approves something else.</summary>
/// <param name="What">The file and why it was skipped.</param>
public sealed record Ignored(string What) : Progress;
