using jd.resolver.expansion;
using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.graph;

/// <summary>
/// The canonical form of a node's config, part of the graph contract: objects with keys in ordinal order, arrays in
/// order, an entries object as its sorted list of name/value items, scalars as written, a resolved string as its value and a pending one as its original text.
/// The node hash is computed over it, so changing this form changes every hash.
/// </summary>
public static class ConfigJson
{
    public static JToken ToToken(ConfigValue value) => value switch
    {
        ConfigObject { AsEntries: true } entries => new JArray(entries.Properties.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new JObject { ["name"] = p.Key, ["value"] = ToToken(p.Value) })),
        ConfigObject obj => new JObject(obj.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new JProperty(p.Key, ToToken(p.Value)))),
        ConfigArray array => new JArray(array.Items.Select(ToToken)),
        ConfigScalar scalar => scalar.Value.DeepClone(),
        ConfigText { Result: Resolved resolved } => new JValue(resolved.Value),
        ConfigText { Result: Pending pending } => new JValue(pending.Original),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown config value"),
    };
}
