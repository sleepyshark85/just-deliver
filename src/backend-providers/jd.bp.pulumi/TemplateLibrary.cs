using jd.core.bp;
using jd.definitionvalidator;
using jd.resolver;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using Newtonsoft.Json.Linq;

namespace jd.bp.pulumi;

/// <summary>
/// The Pulumi YAML template library: each template's <c>Pulumi.yaml</c> by its name, and the offline check that a <see cref="ResolvedGraph"/>
/// fits it: the templates exist, nodes set only declared inputs and all required ones, and node-output references name declared outputs.
/// Lives here because templates are Pulumi-specific.
/// </summary>
public sealed class TemplateLibrary : ITemplateStore
{
    /// <summary>The file a template is, in the directory that names it.</summary>
    public const string TemplateFile = "Pulumi.yaml";

    // The Pulumi YAML keys a template declares its interface with.
    private const string InputsKey = "configuration";
    private const string OutputsKey = "outputs";
    private const string DefaultKey = "default";

    private readonly Dictionary<string, TemplateInterface> _templates = [];
    private readonly Dictionary<string, string> _content = [];
    private readonly List<LoadError> _loadErrors = [];

    private TemplateLibrary()
    {
    }

    /// <summary>What a template declares: its inputs, those without a default, and its outputs.</summary>
    private sealed record TemplateInterface(IReadOnlySet<string> Inputs, IReadOnlySet<string> RequiredInputs, IReadOnlySet<string> Outputs);

    /// <summary>Templates that could not be read, each with its file; <see cref="Check"/> repeats them.</summary>
    public IReadOnlyList<LoadError> Errors => _loadErrors;

    /// <summary>The library of <paramref name="sources"/>: each template's name (<c>azure/log-analytics</c>) and the text of its <see cref="TemplateFile"/>.</summary>
    public static TemplateLibrary Parse(IEnumerable<(string Name, string Yaml)> sources)
    {
        var library = new TemplateLibrary();
        foreach (var (name, yaml) in sources)
        {
            var file = $"{name}/{TemplateFile}";
            if (!YamlSchemaValidator.TryParseYaml(yaml, out var tree, out var error))
            {
                library._loadErrors.Add(new LoadError(file, string.Empty, error));
            }
            else if (tree is JObject template)
            {
                library._content[name] = yaml;
                library._templates[name] = Interface(template);
            }
            else
            {
                library._loadErrors.Add(new LoadError(file, string.Empty, "a template must be a YAML mapping."));
            }
        }

        return library;
    }

    // An input without a default must be supplied by the node.
    private static TemplateInterface Interface(JObject template)
    {
        var inputs = Declared(template, InputsKey).ToList();
        return new TemplateInterface(
            inputs.Select(input => input.Name).ToHashSet(),
            inputs.Where(input => (input.Value as JObject)?.ContainsKey(DefaultKey) != true).Select(input => input.Name).ToHashSet(),
            Declared(template, OutputsKey).Select(output => output.Name).ToHashSet());
    }

    private static IEnumerable<JProperty> Declared(JObject template, string key) => (template[key] as JObject)?.Properties() ?? [];

    /// <inheritdoc/>
    public string GetContent(string templateName) =>
        _content.TryGetValue(templateName, out var content) ? content : throw new KeyNotFoundException($"template '{templateName}' is not in the template library.");

    /// <summary>Every way the graph does not fit the library, with node id, template and key; empty when it fits.</summary>
    public IReadOnlyList<LoadError> Check(ResolvedGraph graph)
    {
        var errors = new List<LoadError>(_loadErrors);
        var byName = graph.Nodes.ToDictionary(n => (n.Scope, n.Name));

        foreach (var node in graph.Nodes)
        {
            if (!_templates.TryGetValue(node.Template, out var template))
            {
                errors.Add(new LoadError(node.Id, "template", $"template '{node.Template}' is not in the template library."));
                continue;
            }

            errors.AddRange(FitErrors(node, template));
            foreach (var (key, value) in node.Config.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                CheckOutputs(node.Scope, References(value), node.Id, key);
            }
        }

        foreach (var (scope, exports) in graph.Exports)
        {
            foreach (var (name, result) in exports)
            {
                CheckOutputs(scope, (result as Pending)?.References ?? (IEnumerable<Reference>)[], $"{graph.Workload}/{graph.Environment}/{scope}", $"exports.{name}");
            }
        }

        return errors;

        // Resource references are the graph builder's concern: their export is checked here, where it is declared.
        void CheckOutputs(string scope, IEnumerable<Reference> references, string file, string location)
        {
            foreach (var reference in references.Where(r => r.Kind == ReferenceKind.Node))
            {
                // The runtime is a node of the workload, whichever scope names it.
                var targetScope = reference.Target == ExpressionEvaluator.RuntimeNode ? GraphBuilder.WorkloadScope : scope;
                if (byName.TryGetValue((targetScope, reference.Target), out var target) && _templates.TryGetValue(target.Template, out var declared) && !declared.Outputs.Contains(reference.Output))
                {
                    errors.Add(new LoadError(file, location, $"references {reference.Target}.{reference.Output}, but template '{target.Template}' (node '{target.Id}') declares no output '{reference.Output}'."));
                }
            }
        }
    }

    // Where the node does not fit its template's inputs or, for the runtime, the outputs the release flow reads. The release flow sets the
    // runtime node's suffix and traffic inputs, so the node need not, and must not (a policy can still set one after the catalog loaded).
    private static IEnumerable<LoadError> FitErrors(GraphNode node, TemplateInterface template)
    {
        var release = node.Release;
        string[] engineInputs = release is null ? [] : [release.SuffixInput, release.TrafficInput];
        string[] engineOutputs = release is null ? [] : [release.TrafficOutput, release.SuffixOutput, release.RevisionOutput, release.FqdnOutput, release.ResourceIdOutput];

        foreach (var key in node.Config.Keys.Where(k => !template.Inputs.Contains(k)).Order(StringComparer.Ordinal))
        {
            yield return new LoadError(node.Id, key, $"template '{node.Template}' does not declare the input '{key}'.");
        }

        foreach (var input in engineInputs.Where(i => !template.Inputs.Contains(i)).Order(StringComparer.Ordinal))
        {
            yield return new LoadError(node.Id, input, $"template '{node.Template}' does not declare the input '{input}' that the runtime's release block names.");
        }

        foreach (var input in engineInputs.Where(node.Config.ContainsKey).Order(StringComparer.Ordinal))
        {
            yield return new LoadError(node.Id, input, $"the release flow sets the input '{input}'; the runtime node must not.");
        }

        foreach (var output in engineOutputs.Where(o => !template.Outputs.Contains(o)))
        {
            yield return new LoadError(node.Id, output, $"template '{node.Template}' declares no output '{output}' that the runtime's release block names.");
        }

        foreach (var key in template.RequiredInputs.Except(engineInputs).Where(k => !node.Config.ContainsKey(k)).Order(StringComparer.Ordinal))
        {
            yield return new LoadError(node.Id, key, $"template '{node.Template}' requires the input '{key}' and the node does not set it.");
        }
    }

    private static IEnumerable<Reference> References(ConfigValue value) => value switch
    {
        ConfigText { Result: Pending pending } => pending.References,
        ConfigObject obj => obj.Properties.Values.SelectMany(References),
        ConfigArray array => array.Items.SelectMany(References),
        _ => [],
    };
}
