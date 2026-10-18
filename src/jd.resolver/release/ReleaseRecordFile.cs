using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using jd.core.records;

namespace jd.resolver.release;

/// <summary>
/// The release records of a label in the <see cref="IRecordStore"/>: <c>set.yaml</c> (the set's exact bytes, ADR 0007) and one JSON file per attempt.
/// Nothing is ever written twice, so an existing name is an <see cref="IOException"/>. Rules: docs/architecture/release-record.md.
/// </summary>
public static class ReleaseRecordFile
{
    /// <summary>The name of the set kept beside a label's records.</summary>
    public const string SetFileName = "set.yaml";

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
        IRecordStore records, string environment, string label, ReadOnlyMemory<byte> setBytes, CancellationToken cancellationToken = default)
    {
        var sha256 = ReleaseDocument.Sha256(setBytes.Span);
        if ((await records.ListAsync(environment, label, cancellationToken)).Contains(SetFileName))
        {
            var kept = ReleaseDocument.Sha256(await records.ReadAsync(environment, label, SetFileName, cancellationToken));
            return (sha256, kept == sha256
                ? null
                : $"the kept {SetFileName} holds a different release set for label {label} on {environment} (sha256 {kept}, this one {sha256}); a label names one set, so use a new label.");
        }

        await records.CreateAsync(environment, label, SetFileName, setBytes, cancellationToken);
        return (sha256, null);
    }

    /// <summary>
    /// Creates the record <c>&lt;UTC time&gt;-&lt;activity&gt;.json</c> and returns where it is; throws <see cref="IOException"/> if it exists,
    /// because a record is never changed. Throws <see cref="ArgumentException"/> for an environment or label that is not a single path segment.
    /// </summary>
    public static Task<string> WriteAsync(
        IRecordStore records, string environment, string label, DateTimeOffset at, string activity, string json, CancellationToken cancellationToken = default) =>
        records.CreateAsync(environment, label, at.UtcDateTime.ToString(NameTimeFormat, CultureInfo.InvariantCulture) + NameEnd(activity), new UTF8Encoding(false).GetBytes(json), cancellationToken);

    /// <summary>A record's text, as a file of it is read (a byte order mark names the encoding). Throws as <see cref="IRecordStore.ReadAsync"/>.</summary>
    internal static async Task<string> ReadTextAsync(IRecordStore records, string environment, string label, string name, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(new MemoryStream(await records.ReadAsync(environment, label, name, cancellationToken)), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
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
    internal static string? SegmentProblem(string segment) =>
        segment.Length == 0 || segment.Contains("..", StringComparison.Ordinal) || segment.AsSpan().IndexOfAny('/', '\\') >= 0
            ? $"'{segment}' cannot name a directory of the release records: it is empty or contains '..', '/' or '\\'."
            : null;

    /// <summary>The hash a record names for a set: the lowercase hex SHA-256 of its bytes.</summary>
    public static string Sha256(ReadOnlySpan<byte> setBytes) => ReleaseDocument.Sha256(setBytes);
}
