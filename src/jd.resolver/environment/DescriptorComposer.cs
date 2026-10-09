using jd.core.bp;
using jd.resolver.catalog;
using jd.resolver.expressions;
using jd.resolver.graph;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace jd.resolver.environment;

/// <summary>The descriptor as YAML text, set exactly when <see cref="Errors"/> is empty and the text passes <see cref="EnvironmentParser"/>.</summary>
public sealed record ComposedDescriptor(string? Yaml, IReadOnlyList<LoadError> Errors);

/// <summary>
/// Composes the environment descriptor of an <see cref="EnvironmentDefinition"/>: evaluates its <c>values</c> against the exports of
/// the resolved graph, merges them into the base descriptor's values and validates the result with <see cref="EnvironmentParser"/>.
/// Pure. Called twice: before deploying (no outputs, to find every error while nothing exists yet) and after, with the node outputs.
/// The environment is the one the graph was resolved in (<see cref="EnvironmentDefinition.Over"/>); its values are the base descriptor's.
/// </summary>
public sealed class DescriptorComposer(EnvironmentDefinition definition, string definitionFile, Catalog catalog, EnvironmentDescriptor environment, ResolvedGraph graph)
{
    private static readonly ISerializer Serializer = new SerializerBuilder().WithQuotingNecessaryStrings().Build();

    // outputs: by node id; null before the deploy, when a value waiting on an output is only checked. Secret and null outputs are
    // dropped here, so a value that needs one stays pending and is reported, and a secret never reaches the descriptor.
    public async Task<ComposedDescriptor> ComposeAsync(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigEntry>>? outputs, CancellationToken cancellationToken = default)
    {
        var errors = new List<LoadError>();
        var evaluator = new ExpressionEvaluator(Context(outputs is null ? new Dictionary<Reference, string>() : ExportValues(outputs, errors)));
        var added = Evaluate(definition.Values, "values", evaluator, outputs is null, errors);
        var merged = Nest(environment.Values);
        Merge(merged, added, string.Empty, errors);

        var document = new JObject
        {
            ["kind"] = "Environment",
            ["name"] = environment.Name,
            ["region"] = environment.Region,
            ["tier"] = environment.Tier,
            ["values"] = merged,
            ["grantable"] = new JArray(environment.Grantable.Concat(definition.Grantable).Distinct().Order(StringComparer.Ordinal)),
        };
        var yaml = Serializer.Serialize(Plain(document));

        // The descriptor rules (reserved keys, dotted keys, grantable paths) are the loader's, applied to the composed document.
        var parsed = await EnvironmentParser.ParseAsync(definitionFile, yaml, cancellationToken);
        errors.AddRange(parsed.Errors);
        return new ComposedDescriptor(errors.Count == 0 ? yaml : null, errors);
    }

    private ExpressionContext Context(IReadOnlyDictionary<Reference, string> known, string currentId = "", IReadOnlySet<string>? nodeNames = null) =>
        new(catalog.Roles, catalog.Naming, environment, graph.Workload, graph.WorkloadTeam, currentId, nodeNames ?? new HashSet<string>(), known);

    // Each export, evaluated with the outputs of its requirement's nodes, is the value of ${resource.<id>.<export>}.
    private Dictionary<Reference, string> ExportValues(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigEntry>> outputs, List<LoadError> errors)
    {
        var known = new Dictionary<Reference, string>();
        foreach (var (scope, exports) in graph.Exports)
        {
            var nodes = graph.Nodes.Where(n => n.Scope == scope).ToList();
            // The filter keeps only entries with a value, so the ! below is safe.
            var nodeOutputs = nodes.Where(n => outputs.ContainsKey(n.Id))
                .SelectMany(n => outputs[n.Id].Where(o => o.Value is { IsSecret: false, Value: not null }).Select(o => (new Reference(ReferenceKind.Node, n.Name, o.Key), o.Value.Value!)))
                .ToDictionary(o => o.Item1, o => o.Item2);
            var evaluator = new ExpressionEvaluator(Context(nodeOutputs, scope, nodes.Select(n => n.Name).ToHashSet()));
            foreach (var (export, result) in exports)
            {
                var resolved = result is Pending pending ? evaluator.Evaluate(pending.Original, definitionFile, $"exports.{export}", errors) : result;
                if (resolved is Resolved value)
                {
                    known[new Reference(ReferenceKind.Resource, scope, export)] = value.Value;
                }
            }
        }

        return known;
    }

    // Same shape as the definition's values with every leaf evaluated. A leaf still waiting is an error after the deploy; before it,
    // it must name an export of a declared requirement (the text stays, as a placeholder the descriptor rules are checked on).
    private JObject Evaluate(JObject node, string path, ExpressionEvaluator evaluator, bool beforeDeploy, List<LoadError> errors)
    {
        var result = new JObject();
        foreach (var property in node.Properties())
        {
            var location = $"{path}.{property.Name}";
            if (property.Value is JObject child)
            {
                result[property.Name] = Evaluate(child, location, evaluator, beforeDeploy, errors);
                continue;
            }

            // The schema makes every leaf of values a string or a mapping (a mapping is handled above), so the cast and the ! are safe.
            var text = (string)property.Value!;
            result[property.Name] = evaluator.Evaluate(text, definitionFile, location, errors) switch
            {
                Resolved resolved => resolved.Value,
                Pending pending => CheckPending(pending, location, beforeDeploy, errors),
                _ => text,
            };
        }

        return result;
    }

    private string CheckPending(Pending pending, string location, bool beforeDeploy, List<LoadError> errors)
    {
        foreach (var reference in pending.References.OrderBy(r => r.Target, StringComparer.Ordinal).ThenBy(r => r.Output, StringComparer.Ordinal))
        {
            var declared = reference.Kind == ReferenceKind.Resource && graph.Exports.TryGetValue(reference.Target, out var exports) && exports.ContainsKey(reference.Output);
            if (!declared)
            {
                errors.Add(new LoadError(definitionFile, location, $"'{reference.Target}.{reference.Output}' is not an export of a requirement of this definition; use ${{resource.<id>.<export>}}."));
            }
            else if (!beforeDeploy)
            {
                errors.Add(new LoadError(definitionFile, location, $"resource.{reference.Target}.{reference.Output} has no usable deployed output (missing, null or secret)."));
            }
        }

        return pending.Original;
    }

    // The base's flat dot paths as nested mappings (the loader guarantees keys have no dots).
    private static JObject Nest(IReadOnlyDictionary<string, string> flat)
    {
        var root = new JObject();
        foreach (var (path, value) in flat)
        {
            var segments = path.Split('.');
            var node = root;
            foreach (var segment in segments[..^1])
            {
                node = (JObject)(node[segment] ??= new JObject());
            }

            node[segments[^1]] = value;
        }

        return root;
    }

    // New keys only: the same leaf, or a leaf where the base has a group (or the reverse), is a collision.
    private void Merge(JObject into, JObject added, string path, List<LoadError> errors)
    {
        foreach (var property in added.Properties())
        {
            var key = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
            if (!into.TryGetValue(property.Name, out var existing))
            {
                into[property.Name] = property.Value;
            }
            else if (existing is JObject existingGroup && property.Value is JObject group)
            {
                Merge(existingGroup, group, key, errors);
            }
            else
            {
                errors.Add(new LoadError(definitionFile, $"values.{key}", $"'{key}' is already a value of the base descriptor; a definition adds new keys only."));
            }
        }
    }

    private static object? Plain(JToken token) => token switch
    {
        JObject obj => obj.Properties().ToDictionary(p => p.Name, p => Plain(p.Value)),
        JArray array => array.Select(Plain).ToList(),
        _ => (string?)token,
    };
}
