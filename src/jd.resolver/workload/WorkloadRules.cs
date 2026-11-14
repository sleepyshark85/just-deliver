using jd.resolver.expressions;

namespace jd.resolver.workload;

/// <summary>
/// Rules of a workload that JSON Schema cannot express, on its bound values: each requirement's effective id is unique (concept 2, ADR 0011), and
/// a variable, workload input crossing into the platform, reads only a requirement's export, as <c>${resource.&lt;id&gt;.&lt;export&gt;}</c>
/// (boundary 6).
/// </summary>
internal static class WorkloadRules
{
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
        var references = workload.Variables.SelectMany(variable => ExpressionEvaluator.Expressions(variable.Value)
            .Select(expression => CheckReference(expression, ids))
            .OfType<string>()
            .Select(message => new LoadError(file, $"{workload.VariablesLocation}.{variable.Key}", message)));
        return [.. duplicates, .. references];
    }

    // What is wrong with one expression in a variable; null when it reads a declared requirement's export, the only expression allowed there.
    private static string? CheckReference((string Written, string Body) reference, List<string> ids)
    {
        if (!reference.Body.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        {
            return $"'{reference.Written}' is not allowed; a variable may only reference a requirement's output, as ${{resource.<id>.<output>}}.";
        }

        var body = reference.Body[ResourcePrefix.Length..];
        var separator = body.IndexOf('.');
        if (separator <= 0 || separator == body.Length - 1)
        {
            return $"'{reference.Written}' must have the form ${{resource.<id>.<output>}}.";
        }

        var id = body[..separator];
        return ids.Contains(id)
            ? null
            : $"'{reference.Written}' refers to '{id}', which no requirement declares (declared ids: {(ids.Count == 0 ? "none" : string.Join(", ", ids))}).";
    }
}
