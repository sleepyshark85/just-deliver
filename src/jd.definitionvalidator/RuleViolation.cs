namespace jd.definitionvalidator;

/// <summary>A rule of a document format that a document breaks: where in the document, and what is wrong.</summary>
/// <param name="Location">The offending field, for example <c>container.variables.DB</c>.</param>
/// <param name="Message">What is wrong and what to do.</param>
public sealed record RuleViolation(string Location, string Message);
