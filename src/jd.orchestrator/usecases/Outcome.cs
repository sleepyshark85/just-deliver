namespace jd.orchestrator.usecases;

/// <summary>
/// How a use case ended: the decision is the use case's, the number a client shows for it is the client's (an exit code, an HTTP status;
/// docs/architecture/application.md section 3).
/// </summary>
public enum Outcome
{
    /// <summary>Everything the use case set out to do was done.</summary>
    Succeeded,

    /// <summary>A plain value of the input was refused before any document was read.</summary>
    InvalidInput,

    /// <summary>A document had errors, a check refused the run, or a step failed.</summary>
    Failed,
}
