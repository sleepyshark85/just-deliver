namespace jd.orchestrator.usecases;

/// <summary>The label's kept set cannot be known: the label is unknown, or its records cannot be read.</summary>
/// <param name="Label">The release label.</param>
/// <param name="Reasons">The records' own words, which say which.</param>
public sealed record UnknownLabel(string Label, IReadOnlyList<string> Reasons) : Problem;
