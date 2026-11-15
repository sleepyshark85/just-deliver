namespace jd.resolver.release;

/// <summary>
/// The status values of a release record's checks (docs/architecture/release-record.md): an OQ, its probe, an IQ and each IQ check. One set,
/// written by the orchestrator and read by <see cref="QualificationReader"/>, as the record's JSON text spells them.
/// </summary>
public static class RecordStatus
{
    /// <summary>The check passed.</summary>
    public const string Passed = "passed";

    /// <summary>The check failed.</summary>
    public const string Failed = "failed";

    /// <summary>The OQ of a workload the deploy did not change, so it did not run again.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>The check did not run, as a step before it failed.</summary>
    public const string NotRun = "notRun";
}
