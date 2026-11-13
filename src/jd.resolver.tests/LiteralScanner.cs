namespace jd.resolver.tests;

// The string literals of a C# source, each with the line it starts on: regular, verbatim and raw strings, and each text segment of an interpolated
// one. A hole is scanned as code, so a literal inside it counts too. Char literals and comments are skipped; an empty literal or segment is not
// listed. Each text is as written, escapes included. Not a C# parser: only enough of the lexical grammar to find where a string starts and ends.
internal sealed class LiteralScanner
{
    // The escapes of a regular string ('\' and the character after it) and of a verbatim one.
    private const string Backslash = "\\";
    private const string DoubledQuote = "\"\"";

    private readonly string _source;
    private readonly List<SourceLiteral> _found = [];
    private int _at;

    private LiteralScanner(string source) => _source = source;

    public static IReadOnlyList<SourceLiteral> Scan(string source)
    {
        var scanner = new LiteralScanner(source);
        scanner.ScanCode();
        if (scanner._at != source.Length)
        {
            throw scanner.Stopped();
        }

        return scanner._found;
    }

    private bool AtEnd => _at >= _source.Length;

    private bool At(string text) => string.CompareOrdinal(_source, _at, text, 0, text.Length) == 0;

    private int RunLength(char c)
    {
        var end = _at;
        while (end < _source.Length && _source[end] == c)
        {
            end++;
        }

        return end - _at;
    }

    // Code up to the end, or up to the '}' that closes the hole it is in, which is left for the string to consume.
    private void ScanCode()
    {
        var depth = 0;
        while (!AtEnd && !(_source[_at] == '}' && depth == 0))
        {
            if (At("//"))
            {
                SkipPast("\n");
            }
            else if (At("/*"))
            {
                SkipPast("*/");
            }
            else if (_source[_at] == '\'')
            {
                SkipCharLiteral();
            }
            else if (!TryString())
            {
                depth += _source[_at] switch { '{' => 1, '}' => -1, _ => 0 };
                _at++;
            }
        }
    }

    private void SkipPast(string end)
    {
        var found = _source.IndexOf(end, _at, StringComparison.Ordinal);
        _at = found < 0 ? _source.Length : found + end.Length;
    }

    private void SkipCharLiteral()
    {
        _at++;
        while (!AtEnd && _source[_at] != '\'' && _source[_at] != '\n')
        {
            _at += At(Backslash) ? 2 : 1;
        }

        // A char literal never spans lines: a quote that finds none is not one (a directive's apostrophe, '#region Don't edit').
        if (AtEnd || _source[_at] == '\n')
        {
            throw Stopped();
        }

        _at++;
    }

    // The scan went wrong where it could not tell: skipping on would hide the rest of the file from the guard.
    private InvalidOperationException Stopped() =>
        new($"The literal scanner stopped at line {LineOf(Math.Min(_at, _source.Length))}, before the end of the source: a stray '}}', or a character " +
            "literal or string it misread (a directive's apostrophe, as in '#region Don't edit', is not code it can skip).");

    // A string starts with its '$' and '@' prefixes, then a quote; a prefix without a quote is code (an '@' identifier).
    private bool TryString()
    {
        var prefixEnd = _at;
        while (prefixEnd < _source.Length && _source[prefixEnd] is '$' or '@')
        {
            prefixEnd++;
        }

        if (prefixEnd == _source.Length || _source[prefixEnd] != '"')
        {
            return false;
        }

        var prefix = _source[_at..prefixEnd];
        _at = prefixEnd;
        var dollars = prefix.Count(c => c == '$');
        if (prefix.Contains('@'))
        {
            ScanQuoted(dollars, DoubledQuote);
        }
        else if (RunLength('"') >= 3)
        {
            ScanRaw(dollars);
        }
        else
        {
            ScanQuoted(dollars, Backslash);
        }

        return true;
    }

    // A regular or verbatim string, up to the quote its escape does not take; when interpolated, '{{' is text and a '{' opens a hole.
    private void ScanQuoted(int dollars, string escape)
    {
        _at++;
        var start = _at;
        while (!AtEnd)
        {
            if (At(escape) || dollars > 0 && At("{{"))
            {
                _at += 2;
            }
            else if (_source[_at] == '"')
            {
                break;
            }
            else if (dollars > 0 && _source[_at] == '{')
            {
                Emit(start);
                ScanHole(dollars);
                start = _at;
            }
            else
            {
                _at++;
            }
        }

        Emit(start);
        _at++;
    }

    // A raw string ends at as many quotes as it starts with; when interpolated, a hole opens at as many braces as it has '$', and the braces before
    // them in the run are text.
    private void ScanRaw(int dollars)
    {
        var quotes = RunLength('"');
        var closing = new string('"', quotes);
        _at += quotes;
        var start = _at;
        while (!AtEnd && !At(closing))
        {
            var braces = RunLength('{');
            if (dollars > 0 && braces >= dollars)
            {
                _at += braces - dollars;
                Emit(start);
                ScanHole(dollars);
                start = _at;
            }
            else
            {
                _at++;
            }
        }

        Emit(start);
        _at += quotes;
    }

    // An interpolation hole: its opening braces, code, and its closing braces, as many of each as the string has '$'.
    private void ScanHole(int braces)
    {
        _at += braces;
        ScanCode();
        _at += braces;
    }

    private void Emit(int start)
    {
        if (_at > start)
        {
            _found.Add(new SourceLiteral(LineOf(start), _source[start.._at]));
        }
    }

    private int LineOf(int position) => _source.AsSpan(0, position).Count('\n') + 1;
}
