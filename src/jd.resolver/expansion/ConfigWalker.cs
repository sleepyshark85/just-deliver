using jd.resolver.expressions;
using Newtonsoft.Json.Linq;

namespace jd.resolver.expansion;

/// <summary>The workload's <c>container.variables</c> and the file they were written in: what <c>fn::entries</c> stands for in the runtime node.</summary>
internal sealed record WorkloadVariables(JObject Map, string File);

/// <summary>Evaluates catalog config (mapping nodes, policy values and added nodes) into <see cref="ConfigValue"/>s; shared by expansion and policies.</summary>
internal static class ConfigWalker
{
    /// <summary>The one config-level form: <c>{ fn::entries: workload.variables }</c> is the workload's variables, deployed as a sorted list of <c>{name, value}</c>.</summary>
    internal const string EntriesKey = "fn::entries";

    /// <summary>The only source of <see cref="EntriesKey"/>.</summary>
    internal const string EntriesSource = "workload.variables";

    private const string VariablesLocation = "container.variables";

    /// <summary>
    /// Walks each field of a node's config; a field that failed to evaluate is omitted (see <see cref="Walk"/>). The form
    /// <c>fn::entries</c> is valid only as the value of a top-level field, and only when <paramref name="variables"/> are given,
    /// which only the runtime node's walk does.
    /// </summary>
    internal static ConfigObject WalkConfig(
        IEnumerable<KeyValuePair<string, JToken>> config, ExpressionEvaluator evaluator, string file, string location, ICollection<LoadError> errors, WorkloadVariables? variables = null)
    {
        var properties = new Dictionary<string, ConfigValue>();
        foreach (var (key, value) in config)
        {
            var walked = variables is not null && IsEntriesForm(value)
                // The variables are walked as plain properties, so a variable may itself be named like the form, and their problems are the workload's.
                ? WalkConfig(Pairs(variables.Map), evaluator, variables.File, VariablesLocation, errors) with { AsEntries = true }
                : Walk(value, evaluator, file, $"{location}.{key}", errors);
            if (walked is not null)
            {
                properties[key] = walked;
            }
        }

        return new ConfigObject(properties);
    }

    private static IEnumerable<KeyValuePair<string, JToken>> Pairs(JObject obj) => obj.Properties().Select(p => KeyValuePair.Create(p.Name, p.Value));

    private static bool IsEntriesForm(JToken value) =>
        value is JObject { Count: 1 } obj && obj[EntriesKey] is JValue { Type: JTokenType.String } source && (string?)source == EntriesSource;

    // Null means a string failed to evaluate. The failure is already in errors; the caller discards or rejects the
    // result as a whole, so the surrounding object or array simply omits the value.
    internal static ConfigValue? Walk(JToken token, ExpressionEvaluator evaluator, string file, string location, ICollection<LoadError> errors)
    {
        switch (token)
        {
            case JObject wrapper when wrapper.ContainsKey(EntriesKey):
                errors.Add(new LoadError(file, location, $"{EntriesKey} is not valid here; the only form is {{ {EntriesKey}: {EntriesSource} }}, as a top-level field of the runtime node's config."));
                return null;
            case JObject obj:
                return WalkConfig(Pairs(obj), evaluator, file, location, errors);
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
