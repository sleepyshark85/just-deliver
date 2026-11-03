using System.Text;
using jd.core.records;

namespace jd.resolver.tests;

// A test fake of the record store: files in a dictionary keyed <environment>/<label>/<name>, which a test edits, copies or renames as someone
// with the records could. It keeps the port's contract: every name is one segment (IRecordStore.SegmentProblem, the guard LocalRecordStore uses),
// an existing name is refused as FileMode.CreateNew refuses it, and a missing one is not found. A key given to Refuse (a file, or
// <environment>/<label> for its listing) cannot be read: as a file or directory without read permission (UnauthorizedAccessException) or
// with a failing disk or share (IOException), the two exceptions the port documents. Linked into jd.orchestrator.tests.
public sealed class MemoryRecordStore : IRecordStore
{
    public enum ReadFault
    {
        AccessDenied,
        IoError,
    }

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ReadFault> _faults = new(StringComparer.Ordinal);

    public void Refuse(string key, ReadFault fault) => _faults[key] = fault;

    public static string Key(string environment, string label, string name) => $"{environment}/{label}/{name}";

    public string Text(string key) => Encoding.UTF8.GetString(Files[key]);

    public void Put(string key, string text) => Files[key] = Encoding.UTF8.GetBytes(text);

    public Task<string> CreateAsync(string environment, string label, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var key = Key(Segment(environment), Segment(label), Segment(name));
        return Files.TryAdd(key, content.ToArray()) ? Task.FromResult(key) : throw new IOException($"The file '{key}' already exists.");
    }

    public Task<IReadOnlyList<string>> ListAsync(string environment, string label, CancellationToken cancellationToken)
    {
        var directory = $"{Segment(environment)}/{Segment(label)}";
        RefuseUnreadable(directory);
        IReadOnlyList<string> names = Files.Keys.Where(k => k.StartsWith(directory + "/", StringComparison.Ordinal)).Select(k => k[(directory.Length + 1)..]).Order(StringComparer.Ordinal).ToList();
        return Task.FromResult(names);
    }

    public Task<byte[]> ReadAsync(string environment, string label, string name, CancellationToken cancellationToken)
    {
        var key = Key(Segment(environment), Segment(label), Segment(name));
        RefuseUnreadable(key);
        return Files.TryGetValue(key, out var content) ? Task.FromResult(content.ToArray()) : throw new FileNotFoundException($"Could not find file '{key}'.", key);
    }

    private void RefuseUnreadable(string key)
    {
        if (_faults.TryGetValue(key, out var fault))
        {
            throw fault == ReadFault.AccessDenied
                ? new UnauthorizedAccessException($"Access to the path '{key}' is denied.")
                : new IOException($"The read of '{key}' failed.");
        }
    }

    private static string Segment(string segment) =>
        IRecordStore.SegmentProblem(segment) is { } problem ? throw new ArgumentException(problem) : segment;
}
