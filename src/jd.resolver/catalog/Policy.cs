using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>An IT Ops rule: <see cref="Set"/> and <see cref="Default"/> config fields of the nodes it matches; <see cref="Require"/>: a component every workload it matches must require (null when none).</summary>
public sealed record Policy(
    string Source,
    string Name,
    string Reason,
    MatchCriteria Match,
    IReadOnlyDictionary<string, JToken> Set,
    IReadOnlyDictionary<string, JToken> Default,
    string? Require);
