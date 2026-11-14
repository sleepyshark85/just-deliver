namespace jd.resolver.catalog;

/// <summary>A policy criterion (<c>match</c>): it holds where <see cref="Role"/>'s value equals <see cref="Value"/>, and everywhere when the policy gives it no value.</summary>
/// <param name="Role">The role whose value the criterion compares with (Appendix B of docs/architecture/documents.md).</param>
/// <param name="Name">The field holding the criterion, as messages name it.</param>
/// <param name="Value">The value the policy compares; null when it gives none.</param>
public sealed record Criterion(string Role, string Name, string? Value);
