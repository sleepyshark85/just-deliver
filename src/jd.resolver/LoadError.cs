using System.Text.RegularExpressions;

namespace jd.resolver;

/// <param name="File">Path of the offending file (catalog-relative for catalog files).</param>
/// <param name="Location">Where in the file, for example <c>nodes.database.config</c>; empty for the document as a whole.</param>
/// <param name="Message">What is wrong and, where useful, what to do.</param>
public sealed partial record LoadError(string File, string Location, string Message)
{
    // Schema errors read "<Kind>: #/<dotted.path>" (nested ones follow in braces); the first path is the location.
    [GeneratedRegex(@"^\w+: #(?<path>\S*)")]
    private static partial Regex SchemaErrorRegex();

    /// <summary>Turns a message from the schema validator into an error located at the offending path.</summary>
    public static LoadError FromSchema(string file, string schemaError)
    {
        var path = SchemaErrorRegex().Match(schemaError).Groups["path"].Value;
        var location = path.Trim('/');
        var detail = string.Join(' ', schemaError.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        return new LoadError(file, location, $"schema violation: {detail}");
    }

    public override string ToString() => Location.Length == 0 ? $"{File}: {Message}" : $"{File}: {Location}: {Message}";
}
