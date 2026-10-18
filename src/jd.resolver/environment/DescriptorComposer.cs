using jd.core.bp;
using jd.resolver.catalog;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace jd.resolver.environment;

/// <summary>The descriptor as YAML text, set exactly when <see cref="Errors"/> is empty and the text passes <see cref="EnvironmentParser"/>.</summary>
public sealed record ComposedDescriptor(string? Yaml, IReadOnlyList<LoadError> Errors);

/// <summary>
/// Composes the environment descriptor of an <see cref="EnvironmentDefinition"/>: every part of the run's graph contributes its exports,
/// evaluated with the deployed outputs and keyed <c>values.&lt;part&gt;.&lt;export&gt;</c>, beside the base descriptor's values (the
/// expander refuses a part the base already has); the result is validated with <see cref="EnvironmentParser"/>. Pure. Called twice: before
/// deploying (no outputs, to find every error while nothing exists yet) and after, with the node outputs. The environment is the one the
/// graph was resolved in (<see cref="EnvironmentDefinition.Over"/>); its values are the base descriptor's.
/// </summary>
public sealed class DescriptorComposer(EnvironmentDefinition definition, string definitionFile, Catalog catalog, EnvironmentDescriptor environment, ResolvedGraph graph)
{
    private static readonly ISerializer Serializer = new SerializerBuilder().WithQuotingNecessaryStrings().Build();

    // outputs: by node id; null before the deploy, when an export waiting on an output is only checked. Secret and null outputs are
    // dropped here, so an export that needs one, directly or through another part's export, stays pending and is reported: a secret
    // never reaches the descriptor (security boundary 12).
    public async Task<ComposedDescriptor> ComposeAsync(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigEntry>>? outputs, CancellationToken cancellationToken = default)
    {
        var errors = new List<LoadError>();
        var known = graph.Nodes.Where(n => outputs?.ContainsKey(n.Id) == true)
            .SelectMany(n => outputs![n.Id].Where(o => o.Value.Value is not null && !o.Value.IsSecret).Select(o => (Reference: new Reference(ReferenceKind.Node, n.Name, o.Key), o.Value.Value)))
            .ToDictionary(o => o.Reference, o => o.Value!);
        var evaluator = new ExpressionEvaluator(new ExpressionContext(
            catalog.Roles, catalog.Naming, environment, graph.Workload, graph.WorkloadTeam, Expander.SubstrateScope, graph.Nodes.Select(n => n.Name).ToHashSet(), known,
            graph.WorkloadImage, graph.WorkloadPort, graph.Parts));
        var values = Nest(environment.Values);
        foreach (var (part, exports) in graph.Parts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var group = new JObject();
            foreach (var (export, text) in exports)
            {
                var location = $"values.{part}.{export}";
                group[export] = evaluator.Evaluate(text, definitionFile, location, errors) switch
                {
                    Resolved resolved => resolved.Value,
                    Pending when outputs is not null => Unusable(part, export, location, errors),
                    _ => text, // before the deploy: the text stays, as a placeholder the descriptor rules are checked on
                };
            }

            values[part] = group;
        }

        // A shared layer offers nothing to workloads: they deploy on an environment, which reads the shared layer as its base.
        var offers = catalog.EnvironmentTypes[environment.Type].Layer == SharedScope.Shared ? [] : definition.Offers;
        var document = new JObject
        {
            ["kind"] = "Environment",
            ["name"] = environment.Name,
            ["region"] = environment.Region,
            ["type"] = environment.Type,
            ["offers"] = new JArray(offers),
            ["values"] = values,
            ["grantable"] = new JArray(environment.Grantable.Concat(definition.Grantable).Distinct().Order(StringComparer.Ordinal)),
        };
        if (environment.Approval is { } approval)
        {
            document["approval"] = approval.ToJson();
        }

        var yaml = Serializer.Serialize(Plain(document));

        // The descriptor rules (reserved keys, dotted keys, grantable paths) are the loader's, applied to the composed document.
        var parsed = await EnvironmentParser.ParseAsync(definitionFile, yaml, cancellationToken);
        errors.AddRange(parsed.Errors);
        return new ComposedDescriptor(errors.Count == 0 ? yaml : null, errors);
    }

    private string Unusable(string part, string export, string location, List<LoadError> errors)
    {
        errors.Add(new LoadError(definitionFile, location, $"export '{export}' of part '{part}' has no usable deployed output (missing, null or secret); a secret never reaches the descriptor."));
        return string.Empty;
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

    private static object? Plain(JToken token) => token switch
    {
        JObject obj => obj.Properties().ToDictionary(p => p.Name, p => Plain(p.Value)),
        JArray array => array.Select(Plain).ToList(),
        _ => ((JValue)token).Value, // a string, or the number or boolean of an approval block
    };
}
