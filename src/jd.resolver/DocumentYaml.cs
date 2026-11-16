using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace jd.resolver;

/// <summary>A written document as YAML text, for the files jd writes (docs/architecture/documents.md section 3).</summary>
internal static class DocumentYaml
{
    /// <summary><paramref name="document"/>'s body as YAML by <paramref name="serializer"/>, each mapping's keys in the order the body holds them.</summary>
    public static string Serialize(ISerializer serializer, Document document) => serializer.Serialize(Plain(document.Body));

    private static object? Plain(JToken token) => token switch
    {
        JObject mapping => mapping.Properties().ToDictionary(property => property.Name, property => Plain(property.Value)),
        JArray list => list.Select(Plain).ToList(),
        _ => ((JValue)token).Value, // a written document holds mappings, lists and scalars only: strings, numbers and booleans
    };
}
