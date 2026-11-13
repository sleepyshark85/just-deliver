namespace jd.resolver;

/// <summary>The documents read from one set of files, such as a catalog, which the name rules check together (<see cref="NameRules"/>).</summary>
/// <param name="Name">What the set is called in errors about it as a whole.</param>
/// <param name="Kinds">The kinds the set holds; a document of another kind is refused on its own and not read for names.</param>
/// <param name="Documents">Every document whose kind could be read, whether or not it passed its schema.</param>
/// <param name="Valid">The documents that passed their schema.</param>
/// <param name="Unreadable">How many files had no readable kind: any of them might have been a missing document.</param>
internal sealed record DocumentSet(string Name, IReadOnlyCollection<string> Kinds, IReadOnlyList<Document> Documents, IReadOnlySet<Document> Valid, int Unreadable);
