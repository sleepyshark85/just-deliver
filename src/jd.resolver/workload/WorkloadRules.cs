using System.Text.RegularExpressions;
using jd.resolver.expressions;

namespace jd.resolver.workload;

/// <summary>
/// Rules of a workload that JSON Schema cannot express, on its bound values: each requirement's effective id is unique (concept 2, ADR 0011), and
/// a variable, workload input crossing into the platform, reads only a requirement's export, as <c>${resource.&lt;id&gt;.&lt;export&gt;}</c>
/// (boundary 6).
/// </summary>
internal static partial class WorkloadRules
{
    // Any expression, with its body captured; only ${resource.<id>.<output>} is allowed in a variable.
    [GeneratedRegex(@"\$\{([^}]*)\}")]
    private static partial Regex ExpressionRegex();

    private const string ResourcePrefix = ExpressionEvaluator.ResourceNamespace + ".";

    /// <summary>Every rule <paramref name="workload"/> breaks, each where it is broken in <paramref name="file"/>; empty when it keeps them all.</summary>
    public static IReadOnlyList<LoadError> Check(Workload workload, string file)
    {
        var declared = workload.Requirements.GroupBy(requirement => requirement.Id).ToList();
        var duplicates = declared.Where(id => id.Count() > 1).Select(id => new LoadError(
            file,
            string.Join(", ", id.Select(requirement => requirement.Location)),
            $"the id '{id.Key}' is used more than once (a requirement without an id uses its type as id). Set distinct 'id's."));
        var ids = declared.Select(id => id.Key).ToList();
        var references = workload.Variables.SelectMany(variable => ExpressionRegex().Matches(variable.Value)
            .Select(reference => CheckReference(reference, ids))
            .OfType<string>()
            .Select(message => new LoadError(file, $"{workload.VariablesLocation}.{variable.Key}", message)));
        return [.. duplicates, .. references];
    }

    // What is wrong with one reference in a variable; null when it reads a declared requirement's export.
    private static string? CheckReference(Match reference, List<string> ids)
    {
        var expression = reference.Groups[1].Value.Trim();
        if (!expression.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        {
            return $"'{reference.Value}' is not allowed; a variable may only reference a requirement's output, as ${{resource.<id>.<output>}}.";
        }

        var body = expression[ResourcePrefix.Length..];
        var separator = body.IndexOf('.');
        if (separator <= 0 || separator == body.Length - 1)
        {
            return $"'{reference.Value}' must have the form ${{resource.<id>.<output>}}.";
        }

        var id = body[..separator];
        return ids.Contains(id)
            ? null
            : $"'{reference.Value}' refers to '{id}', which no requirement declares (declared ids: {(ids.Count == 0 ? "none" : string.Join(", ", ids))}).";
    }
}
