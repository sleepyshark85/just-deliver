namespace jd.core.records;

/// <summary>
/// Where the release, qualification and approval records are kept: a folder of files per environment and label, each file written once
/// (docs/architecture/connections.md). The store knows names and bytes only; what a file means is its reader's. Every name is one path
/// segment: an environment, label or file name that is empty or holds <c>..</c>, <c>/</c> or <c>\</c> throws <see cref="ArgumentException"/>.
/// </summary>
public interface IRecordStore
{
    /// <summary>Creates file <paramref name="name"/> of the label and returns where it is, to show a person. Throws <see cref="IOException"/> when it exists: a record is never changed.</summary>
    Task<string> CreateAsync(string environment, string label, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>The names of the label's files in ordinal order; empty when it has none.</summary>
    Task<IReadOnlyList<string>> ListAsync(string environment, string label, CancellationToken cancellationToken);

    /// <summary>The content of file <paramref name="name"/> of the label. Throws <see cref="IOException"/> (<see cref="FileNotFoundException"/> when it does not exist) or <see cref="UnauthorizedAccessException"/> when it cannot be read.</summary>
    Task<byte[]> ReadAsync(string environment, string label, string name, CancellationToken cancellationToken);
}
