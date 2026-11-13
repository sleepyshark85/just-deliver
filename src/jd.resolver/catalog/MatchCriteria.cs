namespace jd.resolver.catalog;

/// <summary>A policy's criteria key (type, class, environmentType, kind, runtime, template) to the value it must equal.</summary>
public sealed record MatchCriteria(IReadOnlyDictionary<string, string> Criteria)
{
    // What a node-scope policy matches: the requirement's type (a component name) and class, and the environment's type.
    internal const string TypeKey = "type";
    internal const string ClassKey = "class";
    internal const string EnvironmentTypeKey = "environmentType";

    // A node's template, and the runtime scope (kind: runtime) of a workload-scope policy.
    internal const string TemplateKey = "template";
    internal const string KindKey = "kind";
    internal const string RuntimeKey = "runtime";
    internal const string RuntimeKind = "runtime";
}
