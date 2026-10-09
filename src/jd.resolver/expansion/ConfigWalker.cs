using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.expansion;

/// <summary>Evaluates catalog config (mapping nodes, policy values and added nodes) into <see cref="ConfigValue"/>s; shared by expansion and policies.</summary>
internal static class ConfigWalker
{
    /// <summary>Walks each top-level field of a node's config; a field that failed to evaluate is omitted (see <see cref="Walk"/>).</summary>
    internal static ConfigObject WalkConfig(IReadOnlyDictionary<string, JToken> config, ExpressionEvaluator evaluator, string file, string location, ICollection<LoadError> errors)
    {
        var properties = new Dictionary<string, ConfigValue>();
        foreach (var (key, value) in config)
        {
            if (Walk(value, evaluator, file, $"{location}.{key}", errors) is { } walked)
            {
                properties[key] = walked;
            }
        }

        return new ConfigObject(properties);
    }

    // Null means a string failed to evaluate. The failure is already in errors; the caller discards or rejects the
    // result as a whole, so the surrounding object or array simply omits the value.
    internal static ConfigValue? Walk(JToken token, ExpressionEvaluator evaluator, string file, string location, ICollection<LoadError> errors)
    {
        switch (token)
        {
            case JObject obj:
                var properties = new Dictionary<string, ConfigValue>();
                foreach (var property in obj.Properties())
                {
                    if (Walk(property.Value, evaluator, file, $"{location}.{property.Name}", errors) is { } value)
                    {
                        properties[property.Name] = value;
                    }
                }

                return new ConfigObject(properties);
            case JArray array:
                return new ConfigArray(array
                    .Select((item, i) => Walk(item, evaluator, file, $"{location}[{i}]", errors))
                    .OfType<ConfigValue>()
                    .ToList());
            case JValue { Type: JTokenType.String } text:
                var envPaths = new HashSet<string>();
                return evaluator.Evaluate((string?)text ?? string.Empty, file, location, errors, envPaths) is { } result ? new ConfigText(result, envPaths) : null;
            default:
                // JTokens are mutable; never hand out the catalog's own.
                return new ConfigScalar(token.DeepClone());
        }
    }
}
