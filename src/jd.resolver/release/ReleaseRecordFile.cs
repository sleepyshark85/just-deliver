using System.Globalization;
using System.Text;

namespace jd.resolver.release;

/// <summary>
/// The write-once store of release records: <c>&lt;records&gt;/&lt;environment&gt;/&lt;label&gt;/</c> holds <c>set.yaml</c> (the set's exact bytes,
/// ADR 0007) and one JSON file per attempt. Nothing is ever opened for write twice (<see cref="FileMode.CreateNew"/>), so an existing path is
/// an <see cref="IOException"/>. Rules: docs/architecture/release-record.md.
/// </summary>
public static class ReleaseRecordFile
{
    internal const string SetFileName = "set.yaml";

    /// <summary>The activity of a record written by <c>release deploy</c>.</summary>
    public const string DeployActivity = "deploy";

    /// <summary>The activity of a record written by <c>release qualify</c>.</summary>
    public const string QualifyActivity = "qualify";

    /// <summary>The outcome of a deploy in which every workload succeeded.</summary>
    public const string SucceededOutcome = "succeeded";

    /// <summary>The status a passing OQ or IQ has in a record.</summary>
    public const string PassedStatus = "passed";

    /// <summary>The OQ status of a workload that was not changed by the deploy, so its OQ did not run again.</summary>
    public const string UnchangedStatus = "unchanged";

    /// <summary>The format of the times inside a release set and a release record (UTC, to the second).</summary>
    public const string DocumentTimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    // The time in a record's file name (to the millisecond, so two attempts in one second do not collide).
    internal const string NameTimeFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    private static readonly string[] Activities = [DeployActivity, QualifyActivity];

    /// <summary>
    /// Keeps the set's bytes beside the label's records: creates <c>set.yaml</c> on the label's first attempt, and on every later one
    /// checks it holds the same bytes. Returns the set's SHA-256 and no problem, or the SHA-256 and why the label already holds another set.
    /// Throws <see cref="ArgumentException"/> for an environment or label that is not a single path segment.
    /// </summary>
    public static async Task<(string Sha256, string? Problem)> KeepSetAsync(
        string recordsDirectory, string environment, string label, ReadOnlyMemory<byte> setBytes, CancellationToken cancellationToken = default)
    {
        var directory = LabelDirectory(recordsDirectory, environment, label);
        var path = Path.Combine(directory, SetFileName);
        var sha256 = ReleaseDocument.Sha256(setBytes.Span);
        if (File.Exists(path))
        {
            var kept = ReleaseDocument.Sha256(await File.ReadAllBytesAsync(path, cancellationToken));
            return (sha256, kept == sha256
                ? null
                : $"'{path}' holds a different release set for label {label} on {environment} (sha256 {kept}, this one {sha256}); a label names one set, so use a new label.");
        }

        Directory.CreateDirectory(directory);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await stream.WriteAsync(setBytes, cancellationToken);
        return (sha256, null);
    }

    /// <summary>
    /// Creates the record <c>&lt;UTC time&gt;-&lt;activity&gt;.json</c> and returns its path; throws <see cref="IOException"/> if it exists,
    /// because a record is never changed. Throws <see cref="ArgumentException"/> for an environment or label that is not a single path segment.
    /// </summary>
    public static async Task<string> WriteAsync(
        string recordsDirectory, string environment, string label, DateTimeOffset at, string activity, string json, CancellationToken cancellationToken = default)
    {
        var directory = LabelDirectory(recordsDirectory, environment, label);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, at.UtcDateTime.ToString(NameTimeFormat, CultureInfo.InvariantCulture) + NameEnd(activity));
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(json), cancellationToken);
        return path;
    }

    /// <summary>Splits <c>&lt;time&gt;-&lt;activity&gt;.json</c> into its time part and activity; null when the name is not that of a record.</summary>
    internal static (string Time, string Activity)? SplitName(string fileName) =>
        Activities.FirstOrDefault(a => fileName.EndsWith(NameEnd(a), StringComparison.Ordinal)) is { } activity
            ? (fileName[..^NameEnd(activity).Length], activity)
            : null;

    private static string NameEnd(string activity) => $"-{activity}.json";

    // Both names come from validated formats; the store still refuses anything that could leave its directory.
    internal static string LabelDirectory(string recordsDirectory, string environment, string label)
    {
        foreach (var segment in new[] { environment, label })
        {
            if (segment.Length == 0 || segment.Contains("..", StringComparison.Ordinal) || segment.AsSpan().IndexOfAny('/', '\\') >= 0)
            {
                throw new ArgumentException($"'{segment}' cannot name a directory of the release records: it is empty or contains '..', '/' or '\\'.");
            }
        }

        return Path.Combine(recordsDirectory, environment, label);
    }
}
