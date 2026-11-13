using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>
/// Whom a grant may target (docs/architecture/documents.md section 2, concept 9; security boundaries 1 and 2), over the bound part owners. A
/// grant node sits in a class part or the runtime, its template is one of the catalog's grant targets, and the input that names its target is
/// exactly <c>${&lt;node&gt;.&lt;output&gt;}</c> of a node of the same part or the runtime. Its other inputs may read what the component uses.
/// The graph builder checks the target again after policies. Each error is located where the binder read the node's template or config.
/// </summary>
internal static class GrantRules
{
    /// <summary>
    /// Every problem with a grant node of <paramref name="owners"/>' parts: one in a part of one of <paramref name="layers"/>, a template that is
    /// not a key of <paramref name="targets"/> (grant template to the input naming its target, read from the files of kind
    /// <paramref name="targetsKind"/>), or a target input of another form.
    /// </summary>
    public static IReadOnlyList<LoadError> Check(
        IEnumerable<PartOwner> owners, IReadOnlyList<string> layers, IReadOnlyDictionary<string, string> targets, string targetsKind) =>
        [.. from owner in owners
            from part in owner.Parts
            from node in part.Nodes.Values.Where(node => node.Kind == NodeKind.Grant)
            let error = Problem(owner, part, node, layers, targets, targetsKind)
            where error is not null
            select error];

    private static LoadError? Problem(
        PartOwner owner, Part part, Node node, IReadOnlyList<string> layers, IReadOnlyDictionary<string, string> targets, string targetsKind)
    {
        if (layers.Contains(part.Scope))
        {
            return new LoadError(owner.Source, node.Location,
                $"a grant node belongs only in a class part or the runtime, not in a part of the {part.Scope} scope: a grant targets only the workload's own resources.");
        }

        if (!targets.TryGetValue(node.Template, out var input))
        {
            return new LoadError(owner.Source, node.TemplateLocation,
                $"'{node.Template}' is not a grant template in the catalog's {targetsKind} targets ({string.Join(", ", targets.Keys.Order(StringComparer.Ordinal))}); list it with the input that names its target.");
        }

        // A string token's cast cannot give null.
        var isOwnNode = node.Config.GetValueOrDefault(input) is JValue { Type: JTokenType.String } target
            && ExpressionEvaluator.SingleReference((string)target!) is { } reference
            && (part.Nodes.ContainsKey(reference.Node) || reference.Node == ExpressionEvaluator.RuntimeNode);
        return isOwnNode ? null : new LoadError(owner.Source, $"{node.ConfigLocation}.{input}", ExpressionEvaluator.GrantTargetRule(input));
    }
}
