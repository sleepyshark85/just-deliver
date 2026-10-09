using System.Globalization;
using System.Text;
using jd.resolver.release;

namespace jd.cli;

/// <summary>The <c>jd release</c> commands: wires the release builder and file, prints results and returns the exit codes <see cref="Cli"/> documents.</summary>
internal static class ReleaseCli
{
    private const int Success = 0;
    private const int Invalid = 1;
    private const int UsageError = 2;

    // args are the arguments after "release".
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var (command, input, output, problem) = Parse(args);
        if (problem is not null)
        {
            stderr.WriteLine($"jd: {problem}");
            stderr.Write(Cli.Usage);
            return UsageError;
        }

        if (!File.Exists(input))
        {
            stderr.WriteLine($"jd: cannot read '{input}': not found or not a file.");
            return UsageError;
        }

        if (command == "create" && File.Exists(output))
        {
            stderr.WriteLine($"jd: '{output}' already exists; a release set is immutable, so create a new file.");
            return UsageError;
        }

        try
        {
            var result = command == "create"
                ? await ReleaseBuilder.BuildAsync(input, DateTimeOffset.UtcNow, cancellationToken)
                : await ReleaseSetFile.LoadAsync(input, cancellationToken);
            if (result.Set is not { } set)
            {
                foreach (var error in result.Errors)
                {
                    stderr.WriteLine(error);
                }

                return Invalid;
            }

            if (command == "show")
            {
                stdout.Write(Format(set));
                return Success;
            }

            // Parse only accepts create with --out, so the output path is set here.
            await ReleaseSetFile.WriteAsync(set, output!, cancellationToken);
            stdout.WriteLine($"{output}: release {set.Label} with {set.Workloads.Count} workload(s), deploy order {string.Join(", ", set.Order)}.");
            return Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"jd: {ex.Message}");
            return UsageError;
        }
    }

    private static (string? Command, string Input, string? Output, string? Problem) Parse(string[] args)
    {
        var command = args.FirstOrDefault();
        if (command is not ("create" or "show"))
        {
            return (null, string.Empty, null, command is null ? "release needs a command: create or show." : $"unknown release command '{command}'.");
        }

        string? output = null;
        var positional = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--out" && command == "create")
            {
                if (++i == args.Length)
                {
                    return (null, string.Empty, null, "--out needs a value.");
                }

                output = args[i];
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return (null, string.Empty, null, $"unknown option '{args[i]}'.");
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        if (positional.Count != 1)
        {
            return (null, string.Empty, null, command == "create" ? "expected exactly one release manifest." : "expected exactly one release set.");
        }

        return command == "create" && output is null
            ? (null, string.Empty, null, "create needs --out <release-set.yaml>.")
            : (command, positional[0], output, null);
    }

    private static string Format(ReleaseSet set)
    {
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"release {set.Label} (created {set.CreatedAt:yyyy-MM-dd'T'HH:mm:ss'Z'})")
            .AppendLine("deploy order:");
        for (var i = 0; i < set.Order.Count; i++)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {i + 1}. {set.Order[i]}");
        }

        text.AppendLine("workloads:");
        foreach (var workload in set.Workloads)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {workload.Name}")
                .AppendLine(CultureInfo.InvariantCulture, $"    team:       {workload.Team}")
                .AppendLine(CultureInfo.InvariantCulture, $"    sha256:     {workload.DefinitionSha256}")
                .AppendLine(CultureInfo.InvariantCulture, $"    image:      {workload.Image}")
                .AppendLine(CultureInfo.InvariantCulture, $"    depends on: {(workload.DependsOn.Count == 0 ? "(none)" : string.Join(", ", workload.DependsOn))}");
        }

        return text.ToString();
    }
}
