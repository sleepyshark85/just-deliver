using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using jd.resolver.environment;

namespace jd.resolver.expressions;

/// <summary>
/// Evaluates the <c>${…}</c> expressions inside a string. Pure: no I/O. Grammar, namespaces and built-ins
/// are documented in docs/architecture/resolver.md.
/// </summary>
public sealed partial class ExpressionEvaluator(ExpressionContext context)
{
    /// <summary>The reserved node name of the runtime: always in scope, declared only by a Runtime.</summary>
    public const string RuntimeNode = "runtime";

    // Namespaces (first path segment) and the node they reference; part of the expression format.
    internal const string EnvNamespace = "env";
    private const string RoleNamespace = "role";
    private const string WorkloadNamespace = "workload";
    private const string ResourceNamespace = "resource";
    private const string WorkloadNameField = "name";
    private const string WorkloadTeamField = "team";
    private const string WorkloadImageField = "image";
    private const string WorkloadPortField = "port";

    private static readonly Dictionary<string, (int Min, int Max)> Arity = new()
    {
        [BuiltIns.NameFunction] = (1, 1),
        [BuiltIns.GuidFunction] = (1, int.MaxValue),
    };

    private static readonly IReadOnlySet<Reference> NoReferences = new HashSet<Reference>();
    private static readonly IReadOnlySet<string> NoPartReads = new HashSet<string>();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9]*$")]
    private static partial Regex FunctionNamePattern();

    /// <summary>The first segments of a path that are not node names: the namespaces and the runtime node, always in scope.</summary>
    internal static readonly IReadOnlySet<string> ReservedRoots = new HashSet<string> { EnvNamespace, RoleNamespace, WorkloadNamespace, ResourceNamespace, RuntimeNode };

    /// <summary>Whether an expression in <paramref name="text"/> reads a <c>resource.</c> path, as a bare path or a function argument.</summary>
    public static bool ReadsResource(string text) => Paths(text).Any(p => p.StartsWith(ResourceNamespace + ".", StringComparison.Ordinal));

    /// <summary>
    /// The dotted paths the expressions in <paramref name="text"/> read, as bare paths or function arguments. It scans with the evaluator's
    /// own rules (where an expression ends, how arguments split, what a path is), so the two cannot disagree; text the evaluator rejects
    /// is left to it.
    /// </summary>
    internal static IEnumerable<string> Paths(string text)
    {
        var pos = 0;
        while (text.IndexOf("${", pos, StringComparison.Ordinal) is var start and >= 0 && FindClosingBrace(text, start + 2) is var end and >= 0)
        {
            var expression = text[(start + 2)..end].Trim();
            var open = expression.IndexOf('(');
            var arguments = open < 0 ? [expression] : expression.EndsWith(')') ? SplitArguments(expression[(open + 1)..^1]) : [];
            foreach (var path in arguments.Where(a => PathPattern().IsMatch(a)))
            {
                yield return path;
            }

            pos = end + 1;
        }
    }

    /// <summary>
    /// The node and output of <paramref name="text"/> when it is exactly one <c>${&lt;node&gt;.&lt;output&gt;}</c>: no text around it, no function,
    /// no literal, two segments. Null otherwise. What a grant's target must be (docs/architecture/components.md section 3).
    /// </summary>
    internal static (string Node, string Output)? SingleReference(string text) =>
        text.StartsWith("${", StringComparison.Ordinal) && FindClosingBrace(text, 2) == text.Length - 1
        && text[2..^1].Trim() is var path && PathPattern().IsMatch(path) && path.Split('.') is [var node, var output]
            ? (node, output)
            : null;

    // Text is null while the value waits on References. PartReads are the <name>.<export> values read from the descriptor to produce it.
    private sealed record Value(string? Text, IReadOnlySet<Reference> References, IReadOnlySet<string> PartReads);

    /// <summary>
    /// Evaluates every expression in <paramref name="text"/>. Returns null, with every problem added to
    /// <paramref name="errors"/> at <paramref name="file"/>/<paramref name="location"/>, when any expression is invalid.
    /// The deployed parts' exports read (<c>&lt;name&gt;.&lt;export&gt;</c> from the descriptor) are added to <paramref name="partReads"/>:
    /// a resolved value no longer shows where it came from, and the preview shows it.
    /// </summary>
    public EvalResult? Evaluate(string text, string file, string location, ICollection<LoadError> errors, ISet<string>? partReads = null)
    {
        if (EvaluateText(text, message => errors.Add(new LoadError(file, location, message))) is not { } value)
        {
            return null;
        }

        partReads?.UnionWith(value.PartReads);
        return value.References.Count == 0 ? new Resolved(value.Text ?? string.Empty) : new Pending(text, value.References);
    }

    // Every expression in text, joined with the text around them; null when any is invalid.
    private Value? EvaluateText(string text, Action<string> fail)
    {
        var output = new StringBuilder();
        var references = new HashSet<Reference>();
        var partReads = new HashSet<string>();
        var valid = true;
        var pos = 0;
        while (pos < text.Length)
        {
            var start = text.IndexOf("${", pos, StringComparison.Ordinal);
            if (start < 0)
            {
                output.Append(text, pos, text.Length - pos);
                break;
            }

            output.Append(text, pos, start - pos);
            var end = FindClosingBrace(text, start + 2);
            if (end < 0)
            {
                fail($"unterminated expression '{text[start..]}'; expected a closing '}}'.");
                return null;
            }

            var value = EvaluateExpression(text[(start + 2)..end].Trim(), fail);
            if (value is null)
            {
                valid = false;
            }
            else
            {
                references.UnionWith(value.References);
                partReads.UnionWith(value.PartReads);
                output.Append(value.Text);
            }

            pos = end + 1;
        }

        return valid ? new Value(references.Count == 0 ? output.ToString() : null, references, partReads) : null;
    }

    // The first '}' outside a single-quoted literal.
    private static int FindClosingBrace(string text, int from)
    {
        var quoted = false;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (text[i] == '}' && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    private Value? EvaluateExpression(string expression, Action<string> fail)
    {
        if (expression.StartsWith('\''))
        {
            fail($"{expression} is a literal; literals are only valid as function arguments, so write the text outside ${{…}}.");
            return null;
        }

        var open = expression.IndexOf('(');
        return open < 0 ? EvaluateArgument(expression, fail) : EvaluateCall(expression, open, fail);
    }

    private Value? EvaluateCall(string expression, int open, Action<string> fail)
    {
        var function = expression[..open].Trim();
        if (!expression.EndsWith(')') || !FunctionNamePattern().IsMatch(function))
        {
            fail($"'{expression}' is not a valid function call; expected name(arg, ...).");
            return null;
        }

        var arguments = SplitArguments(expression[(open + 1)..^1]);
        if (!Arity.TryGetValue(function, out var arity))
        {
            fail($"unknown function '{function}'; the functions are {string.Join(", ", Arity.Keys.Order())}.");
            return null;
        }

        if (arguments.Count < arity.Min || arguments.Count > arity.Max)
        {
            var expected = arity.Min == arity.Max ? $"exactly {arity.Min}" : $"at least {arity.Min}";
            fail($"{function}() takes {expected} argument(s) but got {arguments.Count}.");
            return null;
        }

        // Evaluate every argument so all errors are reported, not just the first.
        var values = arguments.Select(a => EvaluateArgument(a, fail)).OfType<Value>().ToList();
        if (values.Count < arguments.Count)
        {
            return null;
        }

        var reads = values.SelectMany(v => v.PartReads).ToHashSet();
        var pending = values.Where(v => v.Text is null).SelectMany(v => v.References).ToHashSet();
        if (pending.Count > 0)
        {
            return new Value(null, pending, reads);
        }

        var texts = values.Select(v => v.Text ?? throw new UnreachableException("pending arguments are handled above")).ToList();
        var result = function == BuiltIns.NameFunction ? BuiltIns.Name(context, texts[0], fail) : BuiltIns.Guid(texts);
        return result is null ? null : new Value(result, NoReferences, reads);
    }

    private static List<string> SplitArguments(string inner)
    {
        if (inner.Trim().Length == 0)
        {
            return [];
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in inner)
        {
            if (c == ',' && !quoted)
            {
                arguments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            quoted ^= c == '\'';
            current.Append(c);
        }

        arguments.Add(current.ToString().Trim());
        return arguments;
    }

    private Value? EvaluateArgument(string argument, Action<string> fail)
    {
        if (argument.Length >= 2 && argument[0] == '\'' && argument[^1] == '\'' && !argument[1..^1].Contains('\''))
        {
            return Known(argument[1..^1]);
        }

        if (!PathPattern().IsMatch(argument))
        {
            fail($"'{argument}' is neither a dotted path nor a single-quoted literal.");
            return null;
        }

        return ResolvePath(argument.Split('.'), fail);
    }

    private Value? ResolvePath(string[] segments, Action<string> fail)
    {
        var path = string.Join('.', segments);
        switch (segments[0])
        {
            case EnvNamespace:
                // Only the environment's own fields: a part's exports are read through uses as ${<name>.<export>}, so env.* cannot skip
                // the uses and grant target checks (security boundary 4).
                if (segments.Length == 2 && context.Environment.TryGet(segments[1], out var envValue))
                {
                    return Known(envValue);
                }

                fail($"'{path}' is not an environment field; env.* reads only {string.Join(", ", EnvironmentDescriptor.ReservedKeys.Select(k => $"{EnvNamespace}.{k}"))}; read a deployed part's export as ${{<name>.<export>}} through uses.");
                return null;

            case RoleNamespace:
                if (segments.Length == 2 && context.Roles.TryGetValue(segments[1], out var role))
                {
                    return Known(role);
                }

                fail($"'{path}' is not a role in the catalog; expected role.<name> with one of: {string.Join(", ", context.Roles.Keys.Order())}.");
                return null;

            case WorkloadNamespace:
                if (segments.Length != 2 || segments[1] is not (WorkloadNameField or WorkloadTeamField or WorkloadImageField or WorkloadPortField))
                {
                    fail($"'{path}' is not a workload field; expected {WorkloadNamespace}.{WorkloadNameField}, {WorkloadTeamField}, {WorkloadImageField} or {WorkloadPortField}.");
                    return null;
                }

                var field = segments[1] switch
                {
                    WorkloadNameField => context.WorkloadName,
                    WorkloadTeamField => context.WorkloadTeam,
                    WorkloadImageField => context.WorkloadImage,
                    _ => context.WorkloadPort?.ToString(CultureInfo.InvariantCulture),
                };
                if (field is not null)
                {
                    return Known(field);
                }

                fail($"'{path}' has no value: workload '{context.WorkloadName}' declares no {segments[1]}.");
                return null;

            case ResourceNamespace:
                if (segments.Length == 3)
                {
                    return Await(new Reference(ReferenceKind.Resource, segments[1], segments[2]));
                }

                fail($"'{path}' must have the form {ResourceNamespace}.<id>.<output>.");
                return null;

            default:
                // 'runtime' is a node like any other; it is in scope without being listed. A node is read before a part of the same name:
                // a part's exports read its own nodes (the database part's node is 'database').
                if (segments[0] != RuntimeNode && !context.NodeNames.Contains(segments[0]))
                {
                    return ResolvePart(segments, path, fail);
                }

                if (segments.Length != 2)
                {
                    fail($"'{path}' must have the form <node>.<output>.");
                    return null;
                }

                return Await(new Reference(ReferenceKind.Node, segments[0], segments[1]));
        }
    }

    // A part of this run is read by evaluating its export in place, so the read becomes the node references it is made of
    // (docs/architecture/components.md section 2); the catalog loader keeps uses acyclic, so this ends. Any other part was deployed
    // before: its exports are the descriptor's values, recorded so the preview can show where a value came from.
    private Value? ResolvePart(string[] segments, string path, Action<string> fail)
    {
        if (context.Parts.TryGetValue(segments[0], out var exports))
        {
            if (segments.Length == 2 && exports.TryGetValue(segments[1], out var export))
            {
                return EvaluateText(export, fail);
            }

            fail($"'{path}' is not an export of part '{segments[0]}'; it exports {string.Join(", ", exports.Keys)}.");
            return null;
        }

        if (segments.Length == 2 && context.Environment.Values.TryGetValue(path, out var deployed))
        {
            return new Value(deployed, NoReferences, new HashSet<string> { path });
        }

        fail($"'{path}' refers to unknown node '{segments[0]}', and environment '{context.Environment.Name}' has no value '{path}'; nodes in scope: {string.Join(", ", context.NodeNames.Append(RuntimeNode).Order())}.");
        return null;
    }

    // A deploy-time value: resolved if the caller already knows it, otherwise pending on it.
    private Value Await(Reference reference) =>
        context.KnownOutputs.TryGetValue(reference, out var known) ? Known(known) : new Value(null, new HashSet<Reference> { reference }, NoPartReads);

    private static Value Known(string text) => new(text, NoReferences, NoPartReads);
}
