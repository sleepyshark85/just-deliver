using Xunit;

namespace jd.resolver.tests;

// Each form of C# literal the guard must see, planted in a small source.
public sealed class LiteralScannerTests
{
    private static IEnumerable<string> Texts(string source) => LiteralScanner.Scan(source).Select(literal => literal.Text);

    [Fact]
    public void A_regular_string_ends_at_the_first_quote_no_backslash_escapes()
    {
        Assert.Equal(["a\\\"b\\\\", "c"], Texts("""var a = "a\"b\\"; var b = "c";"""));
    }

    [Fact]
    public void A_verbatim_string_ends_at_a_single_quote_and_a_backslash_does_not_escape()
    {
        Assert.Equal(["a\"\"b\\", "c"], Texts("""var a = @"a""b\"; var b = "c";"""));
    }

    [Fact]
    public void An_interpolated_string_yields_its_text_segments_and_the_literals_in_its_holes()
    {
        const string source = """var a = $"a{x}b{{c}}{(f ? "d" : "e")}{new[] { "g" }.Length}{y:N2}h";""";

        Assert.Equal(["a", "b{{c}}", "d", "e", "g", "h"], Texts(source));
    }

    [Theory]
    [InlineData("""var a = $@"a{x}""b\";""")]
    [InlineData("""var a = @$"a{x}""b\";""")]
    public void An_interpolated_verbatim_string_has_both_rules(string source)
    {
        Assert.Equal(["a", "\"\"b\\"], Texts(source));
    }

    [Fact]
    public void A_raw_string_ends_at_as_many_quotes_as_it_starts_with()
    {
        const string source = """""
            var a = """
                say "hi" or ""hi""
                """;
            var b = """"x"""y"""";
            """"";

        Assert.Equal(["\n    say \"hi\" or \"\"hi\"\"\n    ", "x\"\"\"y"], Texts(source));
    }

    [Fact]
    public void An_interpolated_raw_string_opens_a_hole_at_as_many_braces_as_it_has_dollars()
    {
        const string source = """"
            var a = $"""a{x}"b""";
            var b = $$"""{c}{{x}}d""";
            var c = $$"""{{{x}}}""";
            """";

        Assert.Equal(["a", "\"b", "{c}", "d", "{", "}"], Texts(source));
    }

    [Fact]
    public void A_char_literal_is_skipped_and_its_quote_starts_no_string()
    {
        Assert.Equal(["a"], Texts("""var q = '"'; var e = '\''; var b = '\\'; var s = "a";"""));
    }

    [Fact]
    public void Comments_are_skipped()
    {
        const string source = """
            // "a" isn't a literal
            /* "b"
               isn't either */ var s = "c"; /* unclosed "d"
            """;

        Assert.Equal(["c"], Texts(source));
    }

    [Fact]
    public void An_at_identifier_is_code()
    {
        Assert.Equal(["a"], Texts("""var @class = "a";"""));
    }

    [Fact]
    public void An_empty_literal_or_segment_is_not_listed()
    {
        Assert.Empty(Texts("""var a = ""; var b = @""; var c = $"{x}{y}";"""));
    }

    [Fact]
    public void Each_literal_carries_the_line_it_starts_on()
    {
        const string source = """"
            var a = "a";
            var b = $"b{x
                }c";
            var d = """
                d
                """;
            """";

        Assert.Equal([new SourceLiteral(1, "a"), new SourceLiteral(2, "b"), new SourceLiteral(3, "c"), new SourceLiteral(4, "\n    d\n    ")], LiteralScanner.Scan(source));
    }

    [Fact]
    public void A_stray_closing_brace_fails_naming_its_line_not_skipping_the_rest_of_the_file()
    {
        const string source = """
            var a = "a";
            }
            var b = "b";
            """;

        Assert.Contains("line 2", Assert.Throws<InvalidOperationException>(() => LiteralScanner.Scan(source)).Message);
    }

    [Theory]
    [InlineData("#region Don't edit\nvar a = 'x';\nvar b = \"b\";\n", 1)]
    [InlineData("var a = \"a\";\n#region Don't edit\n", 2)]
    public void An_apostrophe_in_a_directive_fails_naming_its_line_not_skipping_the_rest_of_the_file(string source, int line)
    {
        Assert.Contains($"line {line}", Assert.Throws<InvalidOperationException>(() => LiteralScanner.Scan(source)).Message);
    }

    [Theory]
    [InlineData("var a = \"a", 1)]
    [InlineData("var a = 1;\nvar b = @\"a", 2)]
    [InlineData("var a = $\"a{b}", 1)]
    [InlineData("var a = \"\"\"a", 1)]
    [InlineData("var a = 1;\nvar b = \"\"\"\n  a", 3)]
    public void A_literal_that_runs_past_the_end_fails_naming_the_line_it_stopped_at(string source, int line)
    {
        Assert.Contains($"line {line}", Assert.Throws<InvalidOperationException>(() => LiteralScanner.Scan(source)).Message);
    }
}
