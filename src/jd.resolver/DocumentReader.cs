namespace jd.resolver;

/// <summary>
/// Reads a document another document names by <paramref name="reference"/>, such as a definition a release manifest lists (docs/architecture/application.md
/// section 3). The client decides what a reference resolves to; the name it returns is what errors print, and no content means nothing is there.
/// </summary>
/// <param name="reference">The reference as the naming document holds it.</param>
/// <param name="cancellationToken">Cancels the read.</param>
public delegate Task<(string Name, ReadOnlyMemory<byte>? Content)> DocumentReader(string reference, CancellationToken cancellationToken);
