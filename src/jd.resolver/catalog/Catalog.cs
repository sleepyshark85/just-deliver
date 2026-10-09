using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

// The in-memory form of a catalog directory. Formats: schemas/catalog/*.schema.json.
// Expressions (${...}) and config values are kept exactly as written; the expression
// evaluator interprets them later. Source is the catalog-relative file a record came from.

public sealed record Catalog(
    string Version,
    IReadOnlyList<ResourceType> Types,
    IReadOnlyList<Mapping> Mappings,
    IReadOnlyList<Policy> Policies,
    IReadOnlyDictionary<string, NamingRule> Naming,
    IReadOnlyDictionary<string, string> Roles);

public sealed record ResourceType(
    string Source,
    string Name,
    string Description,
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> Exports);

public enum NodeKind
{
    Create,
    Grant,
}

public sealed record Node(string Template, NodeKind Kind, IReadOnlyDictionary<string, JToken> Config);

/// <summary>Criteria key (type, class, tier, kind, runtime, and for policies template) to the value it must equal.</summary>
public sealed record MatchCriteria(IReadOnlyDictionary<string, string> Criteria)
{
    // The keys a requirement provides to a mapping match; part of the mapping format.
    public const string TypeKey = "type";
    public const string ClassKey = "class";
    public const string TierKey = "tier";
}

public sealed record Mapping(
    string Source,
    MatchCriteria Match,
    IReadOnlyDictionary<string, Node> Nodes,
    IReadOnlyDictionary<string, string> Exports);

public sealed record Policy(
    string Source,
    string Name,
    string Reason,
    MatchCriteria Match,
    IReadOnlyDictionary<string, JToken> Set,
    IReadOnlyDictionary<string, JToken> Default,
    IReadOnlyDictionary<string, Node> Add);

public sealed record NamingRule(string Pattern, int MaxLength, string Allowed);
