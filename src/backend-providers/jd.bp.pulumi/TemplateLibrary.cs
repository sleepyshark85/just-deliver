using jd.definitionvalidator;
using jd.resolver;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using Newtonsoft.Json.Linq;

namespace jd.bp.pulumi;

/// <summary>What a template's <c>Pulumi.yaml</c> declares: its inputs (<c>configuration:</c>), those without a default, and its <c>outputs:</c>.</summary>
internal sealed record TemplateInterface(IReadOnlySet<string> Inputs, IReadOnlySet<string> RequiredInputs, IReadOnlySet<string> Outputs);

/// <summary>
/// The Pulumi YAML template library (<c>catalog/templates/&lt;provider&gt;/&lt;name&gt;/Pulumi.yaml</c>) and the offline check that a
/// <see cref="ResolvedGraph"/> fits it: the templates exist, nodes set only declared inputs and all required ones,
/// and node-output references name declared outputs. Lives here because templates are Pulumi-specific.
/// </summary>
public sealed class TemplateLibrary
{
    private const string TemplateFile = "Pulumi.yaml";

    private readonly Dictionary<string, TemplateInterface> _templates = [];
    private readonly List<LoadError> _loadErrors = [];

    private TemplateLibrary()
    {
    }

    /// <summary>Templates that could not be read, each with its file; <see cref="Check"/> repeats them.</summary>
    public IReadOnlyList<LoadError> Errors => _loadErrors;

    /// <summary>Reads every <c>Pulumi.yaml</c> under <paramref name="root"/>; the template name is its directory relative to the root.</summary>
    public static async Task<TemplateLibrary> LoadAsync(string root, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(root))
        {
            var missing = new TemplateLibrary();
            missing._loadErrors.Add(new LoadError(root, string.Empty, "template directory not found."));
            return missing;
        }

        var sources = new List<(string Name, string Yaml)>();
        foreach (var file in Directory.EnumerateFiles(root, TemplateFile, SearchOption.AllDirectories))
        {
            // Safe: EnumerateFiles yields paths with a file name, so the directory is never null.
            var name = Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Replace(Path.DirectorySeparatorChar, '/');
            sources.Add((name, await File.ReadAllTextAsync(file, cancellationToken)));
        }

        return Parse(sources);
    }

    public static TemplateLibrary Parse(IEnumerable<(string Name, string Yaml)> sources)
    {
        var library = new TemplateLibrary();
        foreach (var (name, yaml) in sources)
        {
            var file = $"{name}/{TemplateFile}";
            try
            {
                if (YamlSchemaValidator.ParseYaml(yaml) is JObject root)
                {
                    library._templates[name] = new TemplateInterface(
                        Names(root, "configuration"),
                        Names(root, "configuration", requiredOnly: true),
                        Names(root, "outputs"));
                }
                else
                {
                    library._loadErrors.Add(new LoadError(file, string.Empty, "a template must be a YAML mapping."));
                }
            }
            catch (Exception ex)
            {
                library._loadErrors.Add(new LoadError(file, string.Empty, YamlSchemaValidator.DescribeYamlError(ex, yaml)));
            }
        }

        return library;
    }

    // An input without a "default" must be supplied by the node.
    private static HashSet<string> Names(JObject root, string block, bool requiredOnly = false) =>
        (root[block] as JObject)?.Properties()
            .Where(p => !requiredOnly || (p.Value as JObject)?.ContainsKey("default") != true)
            .Select(p => p.Name).ToHashSet() ?? [];

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

            foreach (var key in node.Config.Keys.Where(k => !template.Inputs.Contains(k)).Order(StringComparer.Ordinal))
            {
                errors.Add(new LoadError(node.Id, key, $"template '{node.Template}' does not declare the input '{key}'."));
            }

            foreach (var key in template.RequiredInputs.Where(k => !node.Config.ContainsKey(k)).Order(StringComparer.Ordinal))
            {
                errors.Add(new LoadError(node.Id, key, $"template '{node.Template}' requires the input '{key}' and the node does not set it."));
            }

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

        // The runtime node arrives with its own mapping (S15) and resource references are the graph builder's concern.
        void CheckOutputs(string scope, IEnumerable<Reference> references, string file, string location)
        {
            foreach (var reference in references.Where(r => r.Kind == ReferenceKind.Node && r.Target != ExpressionEvaluator.RuntimeNode))
            {
                if (byName.TryGetValue((scope, reference.Target), out var target) && _templates.TryGetValue(target.Template, out var declared) && !declared.Outputs.Contains(reference.Output))
                {
                    errors.Add(new LoadError(file, location, $"references {reference.Target}.{reference.Output}, but template '{target.Template}' (node '{target.Id}') declares no output '{reference.Output}'."));
                }
            }
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
