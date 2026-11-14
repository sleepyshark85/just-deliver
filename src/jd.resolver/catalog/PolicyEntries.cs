namespace jd.resolver.catalog;

/// <summary>A policy's <c>set</c> or <c>default</c>, as the binder read it.</summary>
/// <param name="Name">The field holding them, as messages name it.</param>
/// <param name="Entries">Its entries, in the order written.</param>
public sealed record PolicyEntries(string Name, IReadOnlyList<PolicyEntry> Entries);
