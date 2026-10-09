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

    // Keys only policies use: a node's template, and the runtime scope (kind: runtime) of a workload-scope policy.
    public const string TemplateKey = "template";
    public const string KindKey = "kind";
    public const string RuntimeKey = "runtime";
    public const string RuntimeKind = "runtime";
}

/// <summary>How the platform checks a deployed runtime is healthy: GET <see cref="Path"/> must answer <see cref="ExpectedStatus"/>. Data only; nothing executes it yet.</summary>
public sealed record Probe(string Path, int ExpectedStatus);

/// <summary>A runtime mapping (<c>kind: runtime</c>) has no exports and may have a <see cref="Probe"/>.</summary>
public sealed record Mapping(
    string Source,
    MatchCriteria Match,
    IReadOnlyDictionary<string, Node> Nodes,
    IReadOnlyDictionary<string, string> Exports,
    Probe? Probe = null)
{
    public bool IsRuntime => Match.Criteria.GetValueOrDefault(MatchCriteria.KindKey) == MatchCriteria.RuntimeKind;
}

public sealed record Policy(
    string Source,
    string Name,
    string Reason,
    MatchCriteria Match,
    IReadOnlyDictionary<string, JToken> Set,
    IReadOnlyDictionary<string, JToken> Default,
    IReadOnlyDictionary<string, Node> Add);

public sealed record NamingRule(string Pattern, int MaxLength, string Allowed);
