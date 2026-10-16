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
    private const string SetFileName = "set.yaml";

    private const string TimeFormat = "yyyyMMdd'T'HHmmssfff'Z'";

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
        var path = Path.Combine(directory, $"{at.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture)}-{activity}.json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(json), cancellationToken);
        return path;
    }

    // Both names come from validated formats; the store still refuses anything that could leave its directory.
    private static string LabelDirectory(string recordsDirectory, string environment, string label)
    {
        foreach (var segment in new[] { environment, label })
        {
            if (segment.Length == 0 || segment.Contains("..", StringComparison.Ordinal) || segment.AsSpan().IndexOfAny('/', '\\') >= 0)
            {
                throw new ArgumentException($"'{segment}' cannot name a directory of the record store.");
            }
        }

        return Path.Combine(recordsDirectory, environment, label);
    }
}
