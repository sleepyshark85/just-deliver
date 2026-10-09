using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace jd.resolver.expressions;

/// <summary>
/// Evaluates the <c>${…}</c> expressions inside a string. Pure: no I/O. Grammar, namespaces and built-ins
/// are documented in docs/architecture/resolver.md.
/// </summary>
public sealed partial class ExpressionEvaluator(ExpressionContext context)
{
    // Namespaces (first path segment) and the node they reference; part of the expression format.
    private const string EnvNamespace = "env";
    private const string RoleNamespace = "role";
    private const string WorkloadNamespace = "workload";
    private const string ResourceNamespace = "resource";
    private const string RuntimeNode = "runtime";
    private const string WorkloadNameField = "name";
    private const string WorkloadTeamField = "team";

    private static readonly Dictionary<string, (int Min, int Max)> Arity = new()
    {
        [BuiltIns.NameFunction] = (1, 1),
        [BuiltIns.GuidFunction] = (1, int.MaxValue),
    };

    private static readonly IReadOnlySet<Reference> NoReferences = new HashSet<Reference>();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9]*$")]
    private static partial Regex FunctionNamePattern();

    // Text is null while the value waits on References.
    private sealed record Value(string? Text, IReadOnlySet<Reference> References);

    /// <summary>
    /// Evaluates every expression in <paramref name="text"/>. Returns null, with every problem added to
    /// <paramref name="errors"/> at <paramref name="file"/>/<paramref name="location"/>, when any expression is invalid.
    /// </summary>
    public EvalResult? Evaluate(string text, string file, string location, ICollection<LoadError> errors)
    {
        void Fail(string message) => errors.Add(new LoadError(file, location, message));

        var output = new StringBuilder();
        var references = new HashSet<Reference>();
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
                Fail($"unterminated expression '{text[start..]}'; expected a closing '}}'.");
                return null;
            }

            var value = EvaluateExpression(text[(start + 2)..end].Trim(), Fail);
            if (value is null)
            {
                valid = false;
            }
            else
            {
                references.UnionWith(value.References);
                output.Append(value.Text);
            }

            pos = end + 1;
        }

        if (!valid)
        {
            return null;
        }

        return references.Count == 0 ? new Resolved(output.ToString()) : new Pending(text, references);
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

        var pending = values.Where(v => v.Text is null).SelectMany(v => v.References).ToHashSet();
        if (pending.Count > 0)
        {
            return new Value(null, pending);
        }

        var texts = values.Select(v => v.Text ?? throw new UnreachableException("pending arguments are handled above")).ToList();
        var result = function == BuiltIns.NameFunction ? BuiltIns.Name(context, texts[0], fail) : BuiltIns.Guid(texts);
        return result is null ? null : Known(result);
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
                if (segments.Length > 1 && context.Environment.TryGet(string.Join('.', segments[1..]), out var envValue))
                {
                    return Known(envValue);
                }

                fail($"'{path}' is not a value of environment '{context.Environment.Name}'.");
                return null;

            case RoleNamespace:
                if (segments.Length == 2 && context.Roles.TryGetValue(segments[1], out var role))
                {
                    return Known(role);
                }

                fail($"'{path}' is not a role in the catalog; expected role.<name> with one of: {string.Join(", ", context.Roles.Keys.Order())}.");
                return null;

            case WorkloadNamespace:
                var field = segments.Length == 2
                    ? segments[1] switch { WorkloadNameField => context.WorkloadName, WorkloadTeamField => context.WorkloadTeam, _ => null }
                    : null;
                if (field is not null)
                {
                    return Known(field);
                }

                fail($"'{path}' is not a workload field; expected {WorkloadNamespace}.{WorkloadNameField} or {WorkloadNamespace}.{WorkloadTeamField}.");
                return null;

            case ResourceNamespace:
                if (segments.Length == 3)
                {
                    return Await(new Reference(ReferenceKind.Resource, segments[1], segments[2]));
                }

                fail($"'{path}' must have the form {ResourceNamespace}.<id>.<output>.");
                return null;

            default:
                // 'runtime' is a node like any other; it is in scope without being listed.
                if (segments[0] != RuntimeNode && !context.NodeNames.Contains(segments[0]))
                {
                    fail($"'{path}' refers to unknown node '{segments[0]}'; nodes in scope: {string.Join(", ", context.NodeNames.Append(RuntimeNode).Order())}.");
                    return null;
                }

                if (segments.Length != 2)
                {
                    fail($"'{path}' must have the form <node>.<output>.");
                    return null;
                }

                return Await(new Reference(ReferenceKind.Node, segments[0], segments[1]));
        }
    }

    // A deploy-time value: resolved if the caller already knows it, otherwise pending on it.
    private Value Await(Reference reference) =>
        context.KnownOutputs.TryGetValue(reference, out var known) ? Known(known) : new Value(null, new HashSet<Reference> { reference });

    private static Value Known(string text) => new(text, NoReferences);
}
