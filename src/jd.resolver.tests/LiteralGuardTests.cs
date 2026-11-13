using System.Runtime.CompilerServices;
using Xunit;

namespace jd.resolver.tests;

// The literal guard of docs/architecture/documents.md section 9: no production C# literal names a field the schemas declare. Its allowlist holds
// section 2's exceptions and today's other literals; each later S24 slice removes its entries, and a stale entry fails, so the list only shrinks.
public sealed partial class LiteralGuardTests
{
    // A literal of a production source that names a field; File is relative to src/.
    internal sealed record Finding(string File, int Line, string Literal);

    // A literal names a field when it has no whitespace and one of its dot- or bracket-separated parts is a field name.
    internal static IEnumerable<Finding> Findings(string file, string source, IReadOnlySet<string> fields) =>
        ScanFile(file, source)
            .Where(literal => !literal.Text.Any(char.IsWhiteSpace) && literal.Text.Split(['.', '[', ']']).Any(fields.Contains))
            .Select(literal => new Finding(file, literal.Line, literal.Text));

    // The scanner's failure names a line; among the sources, the file is what lets anyone act on it.
    private static IReadOnlyList<SourceLiteral> ScanFile(string file, string source)
    {
        try
        {
            return LiteralScanner.Scan(source);
        }
        catch (InvalidOperationException stopped)
        {
            throw new InvalidOperationException($"{file}: {stopped.Message}", stopped);
        }
    }

    // Each finding not on the allowlist, and each entry that names no finding, as a line of its own, in full.
    internal static IEnumerable<string> Breaches(IEnumerable<Finding> findings, IReadOnlyCollection<(string File, string Literal)> allowlist)
    {
        var found = findings.ToList();
        var unlisted = found.Where(f => !allowlist.Contains((f.File, f.Literal))).OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line)
            .Select(f => $"{f.File}:{f.Line}: \"{f.Literal}\"");
        var stale = allowlist.Except(found.Select(f => (f.File, f.Literal))).Select(entry => $"stale allowlist entry: {entry.File}: \"{entry.Literal}\"")
            .Order(StringComparer.Ordinal);
        return unlisted.Concat(stale);
    }

    // Assert.Empty would cut each line and list five; this prints them all.
    private static void AssertOnly(IEnumerable<Finding> findings, IReadOnlyCollection<(string File, string Literal)> allowlist)
    {
        var breaches = Breaches(findings, allowlist).ToList();
        if (breaches.Count > 0)
        {
            Assert.Fail(string.Join('\n', breaches));
        }
    }

    // The production sources: every .cs file under src/ outside the test projects and the build output, relative to src/.
    internal static IEnumerable<string> ProductionSources(string sourceRoot) =>
        Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourceRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(file => !file.Split('/').Any(part => part is "bin" or "obj" || part.EndsWith(".tests", StringComparison.Ordinal)));

    // '!': Path.GetDirectoryName returns null only for a root or an empty path; [CallerFilePath] is the full path of this source file, never either.
    private static string RepositoryRoot([CallerFilePath] string testFile = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));

    [Fact]
    public void No_production_literal_names_a_schema_field_beyond_the_allowlist()
    {
        var sourceRoot = Path.Combine(RepositoryRoot(), "src");
        var fields = SchemaFields.In(Path.Combine(RepositoryRoot(), "schemas"));

        var findings = ProductionSources(sourceRoot).SelectMany(file => Findings(file, File.ReadAllText(Path.Combine(sourceRoot, file)), fields));

        AssertOnly(findings, Allowlist);
    }

    [Fact]
    public void Each_allowed_literal_is_in_one_group() => Assert.Equal(Allowlist.Count, Allowlist.Distinct().Count());

    [Fact]
    public void A_field_literal_in_a_planted_source_fails_naming_its_file_line_and_literal()
    {
        const string source = """
            namespace planted;

            internal static class Reader
            {
                // "template" in a comment is not a literal.
                public static string Location(string node) => $"nodes.{node}.template";

                public static string Message(string node) => $"node '{node}' has no template";

                public static string Templates => "templates";
            }
            """;

        var breaches = Breaches(Findings("planted/Reader.cs", source, new HashSet<string> { "template" }), []);

        Assert.Equal(["planted/Reader.cs:6: \".template\""], breaches);
    }

    [Fact]
    public void Each_part_of_a_dotted_or_bracketed_literal_is_checked()
    {
        const string source = """
            var a = "template";
            var b = ".template";
            var c = "template.";
            var d = "nodes[template]";
            var e = "nodes[0].template";
            var f = "nodes.templates";
            var g = "see .template";
            """;

        var literals = Findings("planted.cs", source, new HashSet<string> { "template" }).Select(f => f.Literal);

        Assert.Equal(["template", ".template", "template.", "nodes[template]", "nodes[0].template"], literals);
    }

    [Fact]
    public void A_breach_is_each_unlisted_finding_and_each_stale_entry_in_full()
    {
        var findings = new[] { new Finding("b.cs", 12, "kind"), new Finding("a.cs", 10, "name"), new Finding("a.cs", 9, "name"), new Finding("b.cs", 3, "type") };

        Assert.Equal(["a.cs:9: \"name\"", "a.cs:10: \"name\"", "b.cs:3: \"type\"", "stale allowlist entry: a.cs: \"kind\""], Breaches(findings, [("b.cs", "kind"), ("a.cs", "kind")]));
        Assert.Empty(Breaches(findings, [("a.cs", "name"), ("b.cs", "kind"), ("b.cs", "type")]));
        Assert.Equal("b.cs:3: \"type\"\nstale allowlist entry: c.cs: \"id\"", Assert.ThrowsAny<Exception>(() => AssertOnly(findings, [("a.cs", "name"), ("b.cs", "kind"), ("c.cs", "id")])).Message);
    }

    [Fact]
    public void One_breach_alone_fails_and_no_breach_passes()
    {
        var findings = new[] { new Finding("a.cs", 9, "name") };

        Assert.Equal("a.cs:9: \"name\"", Assert.ThrowsAny<Exception>(() => AssertOnly(findings, [])).Message);
        AssertOnly(findings, [("a.cs", "name")]);
    }

    [Fact]
    public void A_source_the_scanner_cannot_read_fails_naming_its_file_and_line()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Findings("planted/Broken.cs", "var a = \"a\";\n}\n", new HashSet<string> { "a" }));

        Assert.StartsWith("planted/Broken.cs: The literal scanner stopped at line 2", failure.Message);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
    }
}
