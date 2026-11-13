namespace jd.resolver;

/// <summary>What a kind's <c>x-jd</c> annotations say about names (docs/architecture/documents.md section 3, concept 2).</summary>
/// <param name="IsOne">The kind has exactly one document in its set (<c>one</c>).</param>
/// <param name="Defines">The fields that define names (<c>define</c>), one per namespace a field defines in.</param>
/// <param name="Refs">The fields that refer to names (<c>ref</c>).</param>
internal sealed record KindNames(bool IsOne, IReadOnlyList<NameField> Defines, IReadOnlyList<NameField> Refs);
