using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.expansion;

/// <summary>Evaluates catalog config (mapping nodes, policy values and added nodes) into <see cref="ConfigValue"/>s; shared by expansion and policies.</summary>
internal static class ConfigWalker
{
    /// <summary>The one config-level form: <c>{ fn::entries: &lt;map&gt; }</c> is that map, deployed as a sorted list of <c>{name, value}</c>.</summary>
    internal const string EntriesKey = "fn::entries";

    /// <summary>The only source of <see cref="EntriesKey"/>: the workload's <c>container.variables</c>, supplied by the walk of the runtime mapping.</summary>
    internal const string EntriesSource = "workload.variables";

    /// <summary>Walks each top-level field of a node's config; a field that failed to evaluate is omitted (see <see cref="Walk"/>).</summary>
    private static ConfigObject Properties(JObject obj, ExpressionEvaluator evaluator, string file, string location, ICollection<LoadError> errors, JObject? variables)
    {
        var properties = new Dictionary<string, ConfigValue>();
        foreach (var property in obj.Properties())
        {
            if (Walk(property.Value, evaluator, file, $"{location}.{property.Name}", errors, variables) is { } value)
            {
                properties[property.Name] = value;
            }
        }

        return new ConfigObject(properties);
    }

    internal static ConfigObject WalkConfig(
        IReadOnlyDictionary<string, JToken> config, ExpressionEvaluator evaluator, string file, string location, ICollection<LoadError> errors, JObject? variables = null)
    {
        var properties = new Dictionary<string, ConfigValue>();
        foreach (var (key, value) in config)
        {
            if (Walk(value, evaluator, file, $"{location}.{key}", errors, variables) is { } walked)
            {
                properties[key] = walked;
            }
        }

        return new ConfigObject(properties);
    }

    // Null means a string failed to evaluate. The failure is already in errors; the caller discards or rejects the
    // result as a whole, so the surrounding object or array simply omits the value. variables is the workload's, given only
    // by the walk of a runtime mapping's nodes: it is what { fn::entries: workload.variables } stands for, and the form is an
    // error anywhere else.
    internal static ConfigValue? Walk(JToken token, ExpressionEvaluator evaluator, string file, string location, ICollection<LoadError> errors, JObject? variables = null)
    {
        switch (token)
        {
            case JObject wrapper when wrapper.ContainsKey(EntriesKey):
                if (wrapper.Count == 1 && variables is not null && wrapper[EntriesKey] is JValue { Type: JTokenType.String } source && (string?)source == EntriesSource)
                {
                    // The variables are walked as plain properties: a variable may itself be named like the form.
                    return Properties(variables, evaluator, file, $"{location}.{EntriesKey}", errors, null) with { AsEntries = true };
                }

                errors.Add(new LoadError(file, location, $"{EntriesKey} is not valid here; the only form is {{ {EntriesKey}: {EntriesSource} }}, in a runtime mapping's node config."));
                return null;
            case JObject obj:
                return Properties(obj, evaluator, file, location, errors, variables);
            case JArray array:
                return new ConfigArray(array
                    .Select((item, i) => Walk(item, evaluator, file, $"{location}[{i}]", errors, variables))
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
