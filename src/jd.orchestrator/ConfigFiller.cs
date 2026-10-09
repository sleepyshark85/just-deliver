using System.Globalization;
using jd.core.bp;
using jd.resolver;
using jd.resolver.expansion;
using jd.resolver.expressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace jd.orchestrator;

/// <summary>
/// Evaluates a node's config again with the outputs known by now and converts it to backend config entries: a string as it
/// is, a number or boolean in invariant form, an object or array as compact JSON. Values still waiting on a reference are
/// listed in <see cref="Unresolved"/>; evaluation problems in <see cref="Errors"/>. A value that read a secret output is a secret.
/// </summary>
internal sealed class ConfigFiller(ExpressionEvaluator evaluator, string nodeId, IReadOnlySet<Reference> secrets)
{
    private readonly List<LoadError> _errors = [];
    private readonly List<string> _unresolved = [];
    private bool _secret;

    public IReadOnlyList<LoadError> Errors => _errors;

    /// <summary>One entry per field still waiting on a node output: which field and which reference.</summary>
    public IReadOnlyList<string> Unresolved => _unresolved;

    /// <summary>The entries for the fields that are complete; usable only when <see cref="Errors"/> and <see cref="Unresolved"/> are empty.</summary>
    public Dictionary<string, ConfigEntry> Fill(IReadOnlyDictionary<string, ConfigValue> config)
    {
        var entries = new Dictionary<string, ConfigEntry>();
        foreach (var (key, value) in config.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            _secret = false;
            if (Fill(value, key) is { } token)
            {
                entries[key] = new ConfigEntry(token is JValue { Type: JTokenType.String } text ? (string?)text : token.ToString(Formatting.None), _secret);
            }
        }

        return entries;
    }

    // Null when the value, or something inside it, is not complete. Invariant culture: 0.15 must not become "0,15".
    private JToken? Fill(ConfigValue value, string path)
    {
        switch (value)
        {
            case ConfigScalar { Value.Type: JTokenType.Null }:
                _errors.Add(new LoadError(nodeId, path, $"node '{nodeId}': field '{path}' is null; a config value must have a value."));
                return null;
            case ConfigScalar scalar:
                return scalar.Value;
            case ConfigText { Result: Resolved resolved }:
                return new JValue(resolved.Value);
            case ConfigText { Result: Pending pending }:
                return FillPending(pending, path);
            case ConfigObject obj:
                var properties = obj.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, Value: Fill(p.Value, $"{path}.{p.Key}"))).ToList();
                return properties.All(p => p.Value is not null) ? new JObject(properties.Select(p => new JProperty(p.Key, p.Value))) : null;
            case ConfigArray array:
                var items = array.Items.Select((item, i) => Fill(item, $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]")).ToList();
                return items.All(i => i is not null) ? new JArray(items) : null;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, "unknown config value");
        }
    }

    private JToken? FillPending(Pending pending, string path)
    {
        _secret |= pending.References.Overlaps(secrets);
        switch (evaluator.Evaluate(pending.Original, nodeId, path, _errors))
        {
            case Resolved resolved:
                return new JValue(resolved.Value);
            case Pending still:
                _unresolved.AddRange(still.References.OrderBy(r => r.Target, StringComparer.Ordinal).ThenBy(r => r.Output, StringComparer.Ordinal)
                    .Select(r => $"field '{path}' references {r.Target}.{r.Output}"));
                return null;
            default:
                return null;
        }
    }
}
