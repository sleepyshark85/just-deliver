namespace jd.resolver.catalog;

/// <summary>What a node's template does, which decides how the graph treats it.</summary>
public enum NodeKind
{
    /// <summary>Deploys a resource of its own; the default kind. The graph checks no grant target on it, and the runtime node is one.</summary>
    Create,

    /// <summary>Assigns a role on a resource; the catalog's grant targets name the input that holds the resource.</summary>
    Grant,
}
