namespace jd.orchestrator;

/// <summary>Installation qualification: every check that ran, none left out because another failed.</summary>
public sealed record IqResult(IReadOnlyList<IqCheck> Checks, TimeSpan Elapsed)
{
    /// <summary>True when every check passed, so the release is qualified.</summary>
    public bool Passed => Checks.All(c => c.Passed);
}
