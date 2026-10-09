namespace jd.cli;

/// <summary>
/// The arguments the commands that take <c>&lt;file&gt; [--env &lt;file&gt;] [--catalog &lt;dir&gt;] [--preview] [--json]</c> share. Each command decides
/// which of them it accepts and requires; this only reads them and checks that the inputs exist.
/// </summary>
internal sealed record Arguments(IReadOnlyList<string> Positional, string? Environment, string? Catalog, bool Json, bool DryRun)
{
    /// <summary>Reads <paramref name="args"/> from index <paramref name="first"/>; the problem is set when an option is unknown or lacks its value.</summary>
    public static (Arguments? Arguments, string Problem) Scan(string[] args, int first)
    {
        string? environment = null, catalog = null;
        bool json = false, dryRun = false;
        var positional = new List<string>();
        for (var i = first; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--preview":
                    dryRun = true;
                    break;
                case "--env" or "--catalog":
                    if (i + 1 == args.Length)
                    {
                        return (null, $"{args[i]} needs a value.");
                    }

                    if (args[i++] == "--env")
                    {
                        environment = args[i];
                    }
                    else
                    {
                        catalog = args[i];
                    }

                    break;
                case var option when option.StartsWith("--", StringComparison.Ordinal):
                    return (null, $"unknown option '{option}'.");
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        return (new Arguments(positional, environment, catalog, json, dryRun), string.Empty);
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
