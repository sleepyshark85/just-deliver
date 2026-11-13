namespace jd.resolver.tests;

// A string literal, or a text segment of an interpolated one, as written in a C# source, and the line it starts on.
internal sealed record SourceLiteral(int Line, string Text);
