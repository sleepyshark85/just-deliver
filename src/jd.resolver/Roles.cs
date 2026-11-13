namespace jd.resolver;

/// <summary>
/// The roles the engine reads by name (docs/architecture/documents.md Appendix B): the engine's interface, like the expression roots. A schema
/// says which field holds each (<c>is</c>, or <c>select</c> for the value a map is keyed by); no field name is spelled here.
/// </summary>
internal static class Roles
{
    /// <summary>A document whose parts the binder fills (<see cref="catalog.PartOwner"/>).</summary>
    public const string Owner = "owner";

    /// <summary>A part: nodes deployed together and the exports evaluated from them.</summary>
    public const string Part = "part";

    /// <summary>A runtime's part, deployed by the release flow; bound like any other part.</summary>
    public const string RuntimePart = "runtime.part";

    /// <summary>A part's nodes, by name.</summary>
    public const string PartNodes = "part.nodes";

    /// <summary>A part's exports, by name.</summary>
    public const string PartExports = "part.exports";

    /// <summary>The shared resources a part reads; a value held by an object enclosing the part counts for each of its parts.</summary>
    public const string PartReads = "part.reads";

    /// <summary>A part's scope, when the schema does not state it with <c>scope</c>.</summary>
    public const string PartScope = "part.scope";

    /// <summary>A node's template.</summary>
    public const string NodeTemplate = "node.template";

    /// <summary>A node's kind (<see cref="catalog.NodeKind"/>).</summary>
    public const string NodeKind = "node.kind";

    /// <summary>A node's config.</summary>
    public const string NodeConfig = "node.config";

    /// <summary>The exports every selected part of an owner exports exactly.</summary>
    public const string Contract = "contract";

    /// <summary>The layers, as an <c>enum</c> whose listed order is the deploy order.</summary>
    public const string Layer = "layer";

    /// <summary>The environment's type, which selects an implementation.</summary>
    public const string EnvironmentType = "env.type";

    /// <summary>A requirement's class, which selects a class part.</summary>
    public const string RequirementClass = "requirement.class";
}
