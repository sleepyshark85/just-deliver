using jd.core.bp;
using jd.resolver.catalog;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace jd.resolver.environment;

/// <summary>
/// Composes the descriptor of an environment a definition is resolved in: every part of the run's graph contributes its exports,
/// evaluated with the deployed outputs and keyed by part and export in the descriptor's values, beside the base descriptor's values (the
/// expander refuses a part the base already has); the binder writes it (<see cref="Binder.WriteEnvironment"/>) and its text is checked with
/// <see cref="EnvironmentParser"/>. Pure. Called twice: before deploying (no outputs, to find every error while nothing exists yet) and after,
/// with the node outputs. The environment is the one the graph was resolved in (<see cref="EnvironmentDescriptor.Over"/>); its values are the
/// base descriptor's.
/// </summary>
public sealed class DescriptorComposer(string definitionFile, Catalog catalog, EnvironmentDescriptor environment, ResolvedGraph graph)
{
    private static readonly ISerializer Serializer = new SerializerBuilder().WithQuotingNecessaryStrings().Build();

    private static readonly IReadOnlyDictionary<string, ConfigEntry> Unproduced = new Dictionary<string, ConfigEntry>();

    /// <summary>The environment's descriptor, with every part export the deployed outputs settle, or the errors that keep it from being written.</summary>
    /// <param name="outputs">The deployed outputs by node id; null before the deploy, when an export waiting on an output is only checked. Secret
    /// and null outputs are dropped here, so an export that needs one, directly or through another part's export, stays pending and is reported:
    /// a secret never reaches the descriptor (security boundary 12).</param>
    /// <param name="cancellationToken">Cancels the composition.</param>
    public async Task<ComposedDescriptor> ComposeAsync(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigEntry>>? outputs, CancellationToken cancellationToken = default)
    {
        var index = await SchemaIndex.Shipped;
        var binder = new Binder(index);
        // The descriptor's kind is the one kind that binds the deployed values.
        var kind = EnvironmentParser.DescriptorKinds(index).Single();
        var errors = new List<LoadError>();
        var values = ComposeValues(outputs, binder.FieldName(kind, Roles.EnvironmentValues), errors);

        // Workloads deploy in the last layer, so an earlier layer offers them nothing: they deploy on an environment, which reads it as its base.
        var offers = catalog.EnvironmentTypes[environment.Type].Layer != catalog.Layers[^1] ? [] : environment.Offers;
        var written = binder.WriteEnvironment(definitionFile, kind, environment with { Offers = offers }, values);
        errors.AddRange(written.Errors);
        if (written.Value is not { } document)
        {
            return new ComposedDescriptor(null, errors);
        }

        // The written document fits the schema; the descriptor rules (reserved keys, the approval policy) are the loader's, applied to the text
        // as a reader of the descriptor reads it.
        var yaml = DocumentYaml.Serialize(Serializer, document);
        var parsed = await EnvironmentParser.ParseAsync(definitionFile, yaml, cancellationToken);
        errors.AddRange(parsed.Errors);
        return new ComposedDescriptor(errors.Count == 0 ? yaml : null, errors);
    }

    // The base descriptor's values and, beside them, each part's exports evaluated with the usable outputs, by part name. An export is located
    // where the descriptor holds it, under the values' field, which is a root field, so its name is its location.
    private JObject ComposeValues(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigEntry>>? outputs, string valuesField, List<LoadError> errors)
    {
        var evaluator = new ExpressionEvaluator(new ExpressionContext(
            catalog.Roles, catalog.Naming, environment, graph.Owner, new ExpressionScope(Expander.SubstrateScope, graph.Nodes.Select(n => n.Name).ToHashSet(), UsableOutputs(outputs), graph.Parts)));
        var values = Nest(environment.Values);
        foreach (var (part, exports) in graph.Parts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var group = new JObject();
            foreach (var (export, text) in exports)
            {
                var location = $"{valuesField}.{part}.{export}";
                group[export] = evaluator.Evaluate(text, definitionFile, location, errors) switch
                {
                    Resolved resolved => resolved.Value,
                    Pending when outputs is not null => Unusable(part, export, location, errors),
                    _ => text, // before the deploy: the text stays, as a placeholder the descriptor rules are checked on
                };
            }

            values[part] = group;
        }

        return values;
    }

    // The outputs an export may read, by node reference: a secret or null output is left out, so an export needing it stays pending.
    private Dictionary<Reference, string> UsableOutputs(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConfigEntry>>? outputs)
    {
        var usable = new Dictionary<Reference, string>();
        foreach (var node in graph.Nodes)
        {
            foreach (var (name, entry) in outputs?.GetValueOrDefault(node.Id) ?? Unproduced)
            {
                if (entry is { Value: { } value, IsSecret: false })
                {
                    usable.Add(new Reference(ReferenceKind.Node, node.Name, name), value);
                }
            }
        }

        return usable;
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
}
