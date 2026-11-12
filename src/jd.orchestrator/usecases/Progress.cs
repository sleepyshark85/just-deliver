namespace jd.orchestrator.usecases;

/// <summary>
/// What a use case reports while it runs, in the order it happens; a client shows each as it comes, and everything else from the result
/// (docs/architecture/application.md section 3).
/// </summary>
public abstract record Progress;
