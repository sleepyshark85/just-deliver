using System.Text.Json.Nodes;

namespace jd.resolver.tests;

// The field names the schemas declare: every key of every "properties" object in every schema file under a directory, at any depth, $defs included.
internal static class SchemaFields
{
    public static IReadOnlySet<string> In(string schemasDirectory) =>
        Directory.EnumerateFiles(schemasDirectory, "*.schema.json", SearchOption.AllDirectories)
            .SelectMany(file => Declared(JsonNode.Parse(File.ReadAllText(file))))
            .ToHashSet(StringComparer.Ordinal);

    // A "properties" object's values are schemas too, so a field may itself be named "properties".
    private static IEnumerable<string> Declared(JsonNode? node) => node switch
    {
        JsonObject schema => schema.SelectMany(member => member is { Key: "properties", Value: JsonObject fields }
            ? fields.Select(field => field.Key).Concat(fields.SelectMany(field => Declared(field.Value)))
            : Declared(member.Value)),
        JsonArray items => items.SelectMany(Declared),
        _ => [],
    };
}
