using jd.resolver.release;

namespace jd.cli;

/// <summary>
/// The arguments the commands that take <c>&lt;file&gt; [--env &lt;file&gt;] [--catalog &lt;dir&gt;] [--preview] [--json]</c> share, the option names of
/// the others, and the one scan every command's arguments go through. Each command decides which of them it accepts and requires; this only reads
/// them and checks that the inputs exist.
/// </summary>
internal sealed record Arguments(IReadOnlyList<string> Positional, string? Environment, string? Catalog, bool Json, bool DryRun)
{
    internal const string EnvOption = "--env";
    internal const string CatalogOption = "--catalog";
    internal const string JsonOption = "--json";
    internal const string PreviewOption = "--preview";
    internal const string OutOption = "--out";
    internal const string BaseOption = "--base";
    internal const string ForceOption = "--force";

    /// <summary>
    /// What one pass over the arguments found: the positional arguments, the value of each value option given (the last, when one is given twice) and
    /// the flags given.
    /// </summary>
    public sealed record Scanned(IReadOnlyList<string> Positional, IReadOnlyDictionary<string, string> Values, IReadOnlySet<string> Flags);

    /// <summary>Reads a command's <paramref name="args"/>, those after its name; the problem is set when an option is unknown or lacks its value.</summary>
    public static (Arguments? Arguments, string Problem) Scan(string[] args)
    {
        var (scanned, problem) = ScanOptions(args, [EnvOption, CatalogOption], [JsonOption, PreviewOption]);
        if (scanned is null)
        {
            return (null, problem);
        }

        var (positional, values, flags) = scanned;
        return (new Arguments(positional, values.GetValueOrDefault(EnvOption), values.GetValueOrDefault(CatalogOption), flags.Contains(JsonOption), flags.Contains(PreviewOption)), string.Empty);
    }

    /// <summary>
    /// Reads the arguments of a command that takes one release label and the value options <paramref name="valueOptions"/>. The label is null, with the
    /// problem set, when an option is unknown or lacks its value, or there is not exactly one label; whether it can name a release is its use case's.
    /// A value given twice is the last one.
    /// </summary>
    public static (string? Label, IReadOnlyDictionary<string, string> Values, string Problem) ScanOneLabel(string[] args, params string[] valueOptions)
    {
        var (scanned, problem) = ScanOptions(args, valueOptions, []);
        if (scanned is null)
        {
            return (null, new Dictionary<string, string>(), problem);
        }

        return scanned.Positional.Count == 1 ? (scanned.Positional[0], scanned.Values, string.Empty) : (null, scanned.Values, "expected exactly one release label.");
    }

    // TODO(S25f2): goes when jd promote's use case checks its label.
    /// <summary>As <see cref="ScanOneLabel"/>, and the label is null, with the problem set, when it cannot name a release.</summary>
    public static (string? Label, IReadOnlyDictionary<string, string> Values, string Problem) ScanLabel(string[] args, params string[] valueOptions)
    {
        var (label, values, problem) = ScanOneLabel(args, valueOptions);
        return label is not null && ReleaseRecordFile.LabelProblem(label) is { } labelProblem ? (null, values, labelProblem) : (label, values, problem);
    }

    /// <summary>
    /// The one pass every scan makes: a value option takes the argument after it, whatever it is; any other argument starting with "--" that is not a
    /// flag is unknown. Null, with the problem, when an option is unknown or lacks its value.
    /// </summary>
    public static (Scanned? Scanned, string Problem) ScanOptions(string[] args, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flags)
    {
        var positional = new List<string>();
        var values = new Dictionary<string, string>();
        var given = new HashSet<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (valueOptions.Contains(args[i]))
            {
                if (i + 1 == args.Length)
                {
                    return (null, $"{args[i]} needs a value.");
                }

                values[args[i]] = args[++i];
            }
            else if (flags.Contains(args[i]))
            {
                given.Add(args[i]);
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return (null, $"unknown option '{args[i]}'.");
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        return (new Scanned(positional, values, given), string.Empty);
    }

    /// <summary>Each input must be what its role needs: the file and the environment are files, the catalog is a directory. Null when all are fine.</summary>
    public static string? Unreadable(string file, string? environment, string? catalog)
    {
        if (!File.Exists(file))
        {
            return $"cannot read '{file}': not found or not a file.";
        }

        if (environment is not null && !File.Exists(environment))
        {
            return $"cannot read '{environment}': not found or not a file.";
        }

        return catalog is not null && !Directory.Exists(catalog) ? $"cannot read '{catalog}': not found or not a directory." : null;
    }
}
