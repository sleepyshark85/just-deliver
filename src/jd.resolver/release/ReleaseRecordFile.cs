using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>The activity of an approval record written by <c>jd approve</c>; not a release record, so <see cref="SplitName"/> does not know it.</summary>
    public const string ApproveActivity = "approve";

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
        var path = KeptSetPath(recordsDirectory, environment, label);
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
        Activities.FirstOrDefault(a => TimeOf(fileName, a) is not null) is { } activity ? (TimeOf(fileName, activity)!, activity) : null;

    /// <summary>The time part of <c>&lt;time&gt;-&lt;activity&gt;.json</c>; null when the name does not end that way.</summary>
    internal static string? TimeOf(string fileName, string activity) =>
        fileName.EndsWith(NameEnd(activity), StringComparison.Ordinal) ? fileName[..^NameEnd(activity).Length] : null;

    /// <summary>Whether <paramref name="time"/> is a time as <see cref="WriteAsync"/> writes it into a file name.</summary>
    internal static bool IsNameTime(string time) =>
        DateTimeOffset.TryParseExact(time, NameTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);

    internal static string NameEnd(string activity) => $"-{activity}.json";

    // The shape of a release label: the pattern of release.label in schemas/approval-record.schema.json, except that it ends in \z. The schema
    // reads $ as the end of the string; in .NET $ also matches before a final newline, which would let "1.0.0\n" name a directory.
    private const string LabelPattern = @"^[0-9]+\.[0-9]+\.[0-9]+(-[a-z0-9.]+)?\z";

    /// <summary>Why <paramref name="label"/> cannot name a release (it is not label-shaped, or not one path segment); null when it can.</summary>
    public static string? LabelProblem(string label) =>
        SegmentProblem(label) ?? (Regex.IsMatch(label, LabelPattern, RegexOptions.CultureInvariant) ? null : $"'{label}' is not a release label (like 1.0.0 or 1.0.0-rc.1).");

    /// <summary>Why <paramref name="segment"/> cannot name a directory of the store (empty, or contains '..', '/' or '\\'); null when it can.</summary>
    private static string? SegmentProblem(string segment) =>
        segment.Length == 0 || segment.Contains("..", StringComparison.Ordinal) || segment.AsSpan().IndexOfAny('/', '\\') >= 0
            ? $"'{segment}' cannot name a directory of the release records: it is empty or contains '..', '/' or '\\'."
            : null;

    /// <summary>
    /// The directory of a label's files. Both names come from validated formats; the store still refuses anything that could leave its directory:
    /// throws <see cref="ArgumentException"/> for an environment or label that is not a single path segment.
    /// </summary>
    public static string LabelDirectory(string recordsDirectory, string environment, string label)
    {
        foreach (var segment in new[] { environment, label })
        {
            if (SegmentProblem(segment) is { } problem)
            {
                throw new ArgumentException(problem);
            }
        }

        return Path.Combine(recordsDirectory, environment, label);
    }

    /// <summary>The path of the set kept for a label (<c>set.yaml</c> in its directory). Throws as <see cref="LabelDirectory"/>.</summary>
    public static string KeptSetPath(string recordsDirectory, string environment, string label) =>
        Path.Combine(LabelDirectory(recordsDirectory, environment, label), SetFileName);

    /// <summary>The hash a record names for a set: the lowercase hex SHA-256 of its bytes.</summary>
    public static string Sha256(ReadOnlySpan<byte> setBytes) => ReleaseDocument.Sha256(setBytes);
}
