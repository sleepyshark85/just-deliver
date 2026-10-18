using jd.core.records;

namespace jd.resolver.release;

/// <summary>
/// The records on the local disk: <c>&lt;root&gt;/&lt;environment&gt;/&lt;label&gt;/&lt;name&gt;</c>, folders created on the first write. A file is created with
/// <see cref="FileMode.CreateNew"/> and never opened for write again.
/// </summary>
public sealed class LocalRecordStore(string root) : IRecordStore
{
    /// <inheritdoc/>
    public async Task<string> CreateAsync(string environment, string label, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var directory = LabelDirectory(environment, label);
        var path = Path.Combine(directory, Segment(name));
        Directory.CreateDirectory(directory);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await stream.WriteAsync(content, cancellationToken);
        return path;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListAsync(string environment, string label, CancellationToken cancellationToken)
    {
        var directory = LabelDirectory(environment, label);
        IReadOnlyList<string> names = Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToList() : [];
        return Task.FromResult(names);
    }

    /// <inheritdoc/>
    public Task<byte[]> ReadAsync(string environment, string label, string name, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(Path.Combine(LabelDirectory(environment, label), Segment(name)), cancellationToken);

    // Every name comes from a validated format; the store still refuses anything that could leave its folder.
    private string LabelDirectory(string environment, string label) => Path.Combine(root, Segment(environment), Segment(label));

    private static string Segment(string segment) =>
        ReleaseRecordFile.SegmentProblem(segment) is { } problem ? throw new ArgumentException(problem) : segment;
}
