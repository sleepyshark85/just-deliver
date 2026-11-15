namespace jd.definitionvalidator;

/// <summary>The outcome of a validation: each error, none when the document is valid.</summary>
/// <param name="Errors">One entry per error; empty when the document is valid.</param>
public sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    /// <summary>True when the document passed the schema.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>A person-readable verdict, listing each error when the document is invalid.</summary>
    public override string ToString() =>
        IsValid ? "Validation passed" : $"Validation failed:{Environment.NewLine}{string.Join(Environment.NewLine, Errors.Select(e => $"  • {e}"))}";
}
