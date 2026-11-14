using Newtonsoft.Json.Linq;

namespace jd.resolver.catalog;

/// <summary>An entry of a policy's <see cref="PolicyEntries"/>.</summary>
/// <param name="Path">The dotted config path the entry gives, as written.</param>
/// <param name="Value">The value it gives, expressions not yet evaluated.</param>
/// <param name="Location">Where the entry is in the policy's file.</param>
public sealed record PolicyEntry(string Path, JToken Value, string Location);
