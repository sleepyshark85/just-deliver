using jd.bp.pulumi.types;
using jd.core.resources;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.Serialization;

namespace jd.bp.pulumi.tests;

/// <summary>
/// The checks that replace what the C# compiler used to do, now that both layers are YAML.
///
/// Every one is a [Theory] driven by the core catalog rather than a per-type [Fact], so a new
/// type or a new class value inherits its checks with no new test code. Per-type tests would
/// rot; catalog-driven ones cannot.
/// </summary>
public class TypeCoherenceTests
{
    private static readonly ResourceTypeCatalog Core = ResourceTypeLoader.LoadDefault();
    private static readonly BackendTypeCatalog Backend = BackendTypeLoader.LoadDefault();
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>Repository root, shared with the resolver tests.</summary>
    public static string Repository => RepositoryRoot;

    public static TheoryData<string> CoreTypes()
    {
        var data = new TheoryData<string>();
        foreach (var name in Core.Names.Order())
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>Every (type, enum property, permitted value) the core contract declares.</summary>
    public static TheoryData<string, string, string> CoreEnumValues()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var name in Core.Names.Order())
        {
            foreach (var (property, descriptor) in Core.Get(name).Properties.Where(p => p.Value.Type == "enum"))
            {
                foreach (var value in descriptor.Values)
                {
                    data.Add(name, property, value);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CoreTypes))]
    public void Every_core_type_has_a_backend_translation(string type)
    {
        Assert.True(Backend.Supports(type),
            $"jd.core declares '{type}' but this backend has no types/{type}.yml, so a workload requiring it fails at deploy.");
    }

    [Fact]
    public void The_backend_translates_nothing_core_does_not_declare()
    {
        var orphans = Backend.Types.Where(t => !Core.Contains(t)).Order();

        Assert.Empty(orphans);
    }

    [Theory]
    [MemberData(nameof(CoreTypes))]
    public void Every_core_property_is_either_mapped_or_explicitly_waived(string type)
    {
        var translation = Backend.Get(type);
        var accounted = translation.MappedProperties.Concat(translation.Unmapped.Keys).ToHashSet();

        var unaccounted = Core.Get(type).Properties.Keys.Where(p => !accounted.Contains(p)).Order();

        Assert.True(!unaccounted.Any(),
            $"'{type}' declares {string.Join(", ", unaccounted)}, which the translation neither maps nor lists under `unmapped`.");
    }

    [Theory]
    [MemberData(nameof(CoreTypes))]
    public void The_translation_maps_no_property_core_does_not_declare(string type)
    {
        var declared = Core.Get(type).Properties.Keys;
        var declaredOutputs = Core.Get(type).Outputs.Keys;
        var translation = Backend.Get(type);

        var invented = translation.MappedProperties.Where(p => !declared.Contains(p)).Order();
        Assert.True(!invented.Any(),
            $"'{type}' translation maps {string.Join(", ", invented)}, which core does not declare.");

        // `unmapped` may waive a property or an output, but nothing else.
        var unknownWaivers = translation.Unmapped.Keys
            .Where(k => !declared.Contains(k) && !declaredOutputs.Contains(k)).Order();
        Assert.True(!unknownWaivers.Any(),
            $"'{type}' waives {string.Join(", ", unknownWaivers)}, which is neither a property nor an output core declares.");
    }

    [Theory]
    [MemberData(nameof(CoreEnumValues))]
    public void Every_enum_value_has_a_branch(string type, string property, string value)
    {
        var deployments = Backend.Get(type).Deployments.Where(d => d.ParameterSets.ContainsKey(property)).ToList();

        Assert.True(deployments.Count > 0 || Backend.Get(type).Unmapped.ContainsKey(property),
            $"'{type}.{property}' is an enum that no deployment varies on and that is not waived.");

        Assert.All(deployments, deployment =>
            Assert.True(deployment.ParameterSets[property].ContainsKey(value),
                $"'{type}.{property}' permits '{value}' but deployment '{deployment.Id}' has no branch for it."));
    }

    [Theory]
    [MemberData(nameof(CoreEnumValues))]
    public void No_branch_names_a_value_core_forbids(string type, string property, string value)
    {
        var permitted = Core.Get(type).Properties[property].Values;

        foreach (var deployment in Backend.Get(type).Deployments.Where(d => d.ParameterSets.ContainsKey(property)))
        {
            var unknown = deployment.ParameterSets[property].Keys.Where(v => !permitted.Contains(v)).Order();
            Assert.True(!unknown.Any(),
                $"'{deployment.Id}' branches on {string.Join(", ", unknown)} for '{property}', which core forbids.");
        }
    }

    [Theory]
    [MemberData(nameof(CoreTypes))]
    public void Non_enum_properties_are_mapped_by_name_not_by_variant(string type)
    {
        foreach (var (property, descriptor) in Core.Get(type).Properties)
        {
            if (descriptor.Type == "enum")
            {
                continue;
            }

            foreach (var deployment in Backend.Get(type).Deployments)
            {
                Assert.False(deployment.ParameterSets.ContainsKey(property),
                    $"'{type}.{property}' is {descriptor.Type}, not an enum, so it belongs under `parameters` rather than `parameter_sets`.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(CoreTypes))]
    public void Translated_outputs_are_a_subset_of_what_core_declares(string type)
    {
        var declared = Core.Get(type).Outputs.Keys;
        var produced = Backend.Get(type).Outputs.Keys;

        var undeclared = produced.Where(o => !declared.Contains(o)).Order();
        Assert.Empty(undeclared);
    }

    [Theory]
    [MemberData(nameof(CoreTypes))]
    public void Non_secret_outputs_are_all_produced(string type)
    {
        // Secret outputs may legitimately be unmapped - Cosmos with managed identity has no
        // connection string to hand over - but a plain output nobody produces means a
        // workload reference silently resolves to null.
        var missing = Core.Get(type).Outputs
            .Where(o => !o.Value.Secret && !Backend.Get(type).Outputs.ContainsKey(o.Key))
            .Select(o => o.Key)
            .Order();

        Assert.Empty(missing);
    }

    // --- translation against the Pulumi definitions it binds to ---

    [Fact]
    public void Every_referenced_definition_exists()
    {
        foreach (var deployment in Backend.AllDeployments)
        {
            Assert.True(File.Exists(DefinitionPath(deployment.Definition)),
                $"deployment '{deployment.Id}' references definition '{deployment.Definition}', which does not exist.");
        }
    }

    [Fact]
    public void Every_parameter_exists_in_the_definition_it_binds_to()
    {
        foreach (var deployment in Backend.AllDeployments)
        {
            var configuration = DefinitionConfiguration(deployment.Definition);

            var bound = deployment.AllParameterNames;

            foreach (var parameter in bound)
            {
                // The executor expands `role` into roleDefinitionId using the target scope.
                var expected = parameter == "role" ? "roleDefinitionId" : parameter;

                Assert.True(configuration.Contains(expected),
                    $"'{deployment.Id}' binds '{parameter}', which '{deployment.Definition}' does not accept. It accepts: {string.Join(", ", configuration.Order())}.");
            }
        }
    }

    [Fact]
    public void Every_output_expression_names_a_real_deployment_output()
    {
        var definitionByDeployment = Backend.AllDeployments.ToDictionary(d => d.Id, d => d.Definition);
        var expressions = Backend.Types
            .SelectMany(t => Backend.Get(t).Outputs.Select(o => (Type: t, Key: o.Key, Expression: o.Value)))
            .Concat(Backend.AllDeployments.SelectMany(d =>
                d.Parameters.Select(p => (Type: d.Id, Key: p.Key, Expression: p.Value))))
            .Concat(Backend.AllDeployments.SelectMany(d =>
                d.ParameterSets.SelectMany(sw => sw.Value.SelectMany(branch =>
                    branch.Value.Select(p => (Type: d.Id, Key: $"{sw.Key}/{branch.Key}/{p.Key}", Expression: p.Value))))));

        foreach (var (owner, key, expression) in expressions)
        {
            foreach (var match in Regex.Matches(expression, @"\$\{deployment\.([a-z0-9-]+)\.([A-Za-z]+)\}").Cast<Match>())
            {
                var (id, output) = (match.Groups[1].Value, match.Groups[2].Value);

                Assert.True(definitionByDeployment.ContainsKey(id),
                    $"'{owner}.{key}' references deployment '{id}', which is not declared anywhere.");

                Assert.True(DefinitionOutputs(definitionByDeployment[id]).Contains(output),
                    $"'{owner}.{key}' reads '{id}.{output}', but '{definitionByDeployment[id]}' outputs: {string.Join(", ", DefinitionOutputs(definitionByDeployment[id]).Order())}.");
            }
        }
    }

    [Fact]
    public void Every_role_alias_is_declared_in_the_shared_roles_table()
    {
        foreach (var deployment in Backend.AllDeployments)
        {
            if (deployment.Parameters.TryGetValue("role", out var alias))
            {
                Assert.True(Backend.Shared.Roles.ContainsKey(alias),
                    $"'{deployment.Id}' uses role '{alias}', which is not in the shared roles table.");
            }
        }
    }

    [Fact]
    public void Every_environment_key_referenced_is_present_in_every_environment()
    {
        var referenced = Backend.AllDeployments
            .SelectMany(d => d.Parameters.Values.Concat(d.ParameterSets.Values.SelectMany(v => v.Values).SelectMany(p => p.Values)))
            .SelectMany(v => Regex.Matches(v, @"\$\{env\.([a-z_]+)\}").Cast<Match>())
            .Select(m => m.Groups[1].Value)
            .Distinct();

        foreach (var (environment, settings) in Backend.Shared.Environments)
        {
            foreach (var key in referenced)
            {
                Assert.True(settings.ContainsKey(key),
                    $"'{key}' is referenced but missing from environments.{environment}.");
            }
        }
    }

    // --- helpers ---

    private static string DefinitionPath(string definition) =>
        Path.Combine(RepositoryRoot, Backend.Shared.DefinitionsRoot, definition, "Pulumi.yaml");

    private static HashSet<string> DefinitionConfiguration(string definition) =>
        DefinitionSection(definition, "configuration");

    private static HashSet<string> DefinitionOutputs(string definition) =>
        DefinitionSection(definition, "outputs");

    private static HashSet<string> DefinitionSection(string definition, string section)
    {
        var document = new DeserializerBuilder().Build()
            .Deserialize<Dictionary<string, object>>(File.ReadAllText(DefinitionPath(definition)));

        return document.TryGetValue(section, out var value) && value is Dictionary<object, object> map
            ? map.Keys.Select(k => k.ToString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "schemas"))
                && File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
