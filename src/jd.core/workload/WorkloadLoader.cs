using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace jd.core.workload;

/// <summary>
/// Reads a workload definition. A resource's properties depend on its type, so resources are
/// read untyped and projected - the type contract, not a C# shape, decides what is valid.
/// </summary>
public class WorkloadLoader
{
    // Scalars stay as their raw text, so a YAML `true` reaches Pulumi config as "true" rather
    // than .NET's "True", which a `type: Boolean` program would reject.
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static WorkloadDocument Load(string yaml)
    {
        var raw = Deserializer.Deserialize<Dictionary<string, object?>>(yaml)
            ?? throw new InvalidOperationException("Workload definition is empty.");

        var document = new WorkloadDocument
        {
            ApiVersion = Scalar(raw, "apiVersion"),
            Kind = Scalar(raw, "kind"),
            Metadata = Metadata(raw),
        };

        if (raw.TryGetValue("resources", out var resources) && resources is System.Collections.IEnumerable list)
        {
            foreach (var entry in list.Cast<object?>())
            {
                document.Resources.Add(Resource(entry));
            }
        }

        return document;
    }

    private static WorkloadMetadata Metadata(Dictionary<string, object?> raw)
    {
        var map = AsMap(raw.GetValueOrDefault("metadata"));

        return new WorkloadMetadata
        {
            Name = map.GetValueOrDefault("name")?.ToString() ?? "",
            Team = map.GetValueOrDefault("team")?.ToString() ?? "",
            Environment = map.GetValueOrDefault("environment")?.ToString() ?? "",
        };
    }

    private static WorkloadResource Resource(object? entry)
    {
        var map = AsMap(entry);
        var resource = new WorkloadResource
        {
            Name = map.GetValueOrDefault("name")?.ToString() ?? "",
            Type = map.GetValueOrDefault("type")?.ToString() ?? "",
        };

        // Everything that is not name or type is a property of the type.
        foreach (var (key, value) in map)
        {
            if (key is not ("name" or "type"))
            {
                resource.Properties[key] = value;
            }
        }

        return resource;
    }

    private static string Scalar(Dictionary<string, object?> raw, string key) =>
        raw.GetValueOrDefault(key)?.ToString() ?? "";

    private static Dictionary<string, object?> AsMap(object? value) =>
        value is System.Collections.IDictionary raw
            ? raw.Keys.Cast<object>().ToDictionary(k => k.ToString()!, k => (object?)raw[k])
            : new Dictionary<string, object?>();
}
