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
    string? Require)
{
    /// <summary>The policy a schema-valid policy <paramref name="document"/> is. Read by its fields until S24d binds the policy roles.</summary>
    internal static Policy Read(Document document)
    {
        var body = document.Body;
        return new Policy(
            document.Source,
            Text(body["name"]),
            Text(body["reason"]),
            new MatchCriteria(Map(body["match"]).ToDictionary(criterion => criterion.Key, criterion => Text(criterion.Value))),
            Map(body["set"]),
            Map(body["default"]),
            (string?)body["require"]);
    }

    private static string Text(JToken? token) => (string?)token ?? string.Empty;

    private static Dictionary<string, JToken> Map(JToken? token) =>
        token is JObject map ? map.Properties().ToDictionary(entry => entry.Name, entry => entry.Value) : [];
}
