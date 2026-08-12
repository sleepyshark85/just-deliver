using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace jd.core.resources;

/// <summary>Reads resource type contracts from YAML and checks they are internally coherent.</summary>
public class ResourceTypeLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Types shipped with jd.core, from the resource-types folder beside the assembly.</summary>
    public static ResourceTypeCatalog LoadDefault() =>
        LoadDirectory(Path.Combine(AppContext.BaseDirectory, "resource-types"));

    public static ResourceTypeCatalog LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Resource type directory '{directory}' not found.");
        }

        var types = Directory.GetFiles(directory, "*.yml")
            .OrderBy(path => path)
            .Select(path => Load(File.ReadAllText(path), Path.GetFileName(path)))
            .ToList();

        return new ResourceTypeCatalog(types);
    }

    public static ResourceTypeDefinition Load(string yaml, string source = "<inline>")
    {
        var definition = Deserializer.Deserialize<ResourceTypeDefinition>(yaml)
            ?? throw new InvalidOperationException($"'{source}' is empty.");

        Validate(definition, source);
        return definition;
    }

    private static void Validate(ResourceTypeDefinition definition, string source)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new InvalidOperationException($"'{source}' declares no name.");
        }

        var expected = Path.GetFileNameWithoutExtension(source);
        if (source != "<inline>" && definition.Name != expected)
        {
            throw new InvalidOperationException(
                $"'{source}' declares name '{definition.Name}' but the filename says '{expected}'.");
        }

        foreach (var (name, property) in definition.Properties)
        {
            var isEnum = property.Type == "enum";

            if (isEnum && property.Values.Count == 0)
            {
                throw new InvalidOperationException(
                    $"'{source}': property '{name}' is an enum with no values.");
            }

            if (!isEnum && property.Values.Count > 0)
            {
                throw new InvalidOperationException(
                    $"'{source}': property '{name}' lists values but is not an enum.");
            }

            if (isEnum && property.Default is not null && !property.Values.Contains(property.Default))
            {
                throw new InvalidOperationException(
                    $"'{source}': property '{name}' defaults to '{property.Default}', which is not one of {string.Join(", ", property.Values)}.");
            }

            // A required property with a default can never be unsatisfied, so one of the two
            // is redundant and the pair is probably a mistake.
            if (property.Required && property.Default is not null)
            {
                throw new InvalidOperationException(
                    $"'{source}': property '{name}' is required and also has a default.");
            }
        }
    }
}
