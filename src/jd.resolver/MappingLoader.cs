using jd.resolver.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace jd.resolver;

/// <summary>Reads definitions/mappings into models and enforces folder-level invariants.</summary>
public class MappingLoader
{
    private const string SharedFileName = "_shared.yml";

    // Mapping documents are snake_case throughout (resource_type, override_whitelist).
    private static readonly IDeserializer MappingDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Workload documents are camelCase, apart from override_reason which is aliased.
    private static readonly IDeserializer WorkloadDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static WorkloadDocument LoadWorkload(string yaml) =>
        WorkloadDeserializer.Deserialize<WorkloadDocument>(yaml)
        ?? throw new ResolutionException("Workload definition is empty.");

    public async Task<(SharedSettings Shared, Dictionary<string, MappingDocument> Mappings)> LoadAsync(string mappingsDirectory)
    {
        var sharedPath = Path.Combine(mappingsDirectory, SharedFileName);
        if (!File.Exists(sharedPath))
        {
            throw new ResolutionException($"Shared mapping settings not found at '{sharedPath}'.");
        }

        var shared = MappingDeserializer.Deserialize<SharedSettings>(await File.ReadAllTextAsync(sharedPath))
            ?? throw new ResolutionException($"'{SharedFileName}' is empty.");

        var mappings = new Dictionary<string, MappingDocument>();
        var deploymentOwner = new Dictionary<string, string>();

        foreach (var path in Directory.GetFiles(mappingsDirectory, "*.yml").OrderBy(p => p))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith('_'))
            {
                continue;
            }

            var mapping = MappingDeserializer.Deserialize<MappingDocument>(await File.ReadAllTextAsync(path))
                ?? throw new ResolutionException($"'{fileName}' is empty.");

            var expected = Path.GetFileNameWithoutExtension(fileName);
            if (mapping.ResourceType != expected)
            {
                throw new ResolutionException(
                    $"'{fileName}' declares resource_type '{mapping.ResourceType}' but the filename says '{expected}'.");
            }

            // Deployment ids are global across the folder, so a duplicate would silently
            // rebind a ${deployment.*} reference rather than collide visibly.
            foreach (var deployment in mapping.Deployments)
            {
                if (deploymentOwner.TryGetValue(deployment.Id, out var other))
                {
                    throw new ResolutionException(
                        $"Duplicate deployment id '{deployment.Id}' in '{fileName}' and '{other}'. Ids must be unique across all mappings.");
                }

                deploymentOwner[deployment.Id] = fileName;
            }

            mappings[mapping.ResourceType] = mapping;
        }

        foreach (var type in shared.PolicyAttached)
        {
            if (!mappings.ContainsKey(type))
            {
                throw new ResolutionException($"policy_attached lists '{type}' but there is no {type}.yml mapping.");
            }
        }

        return (shared, mappings);
    }
}
