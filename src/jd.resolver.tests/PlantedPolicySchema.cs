using Newtonsoft.Json.Linq;

namespace jd.resolver.tests;

// A Binder over the shipped policy schema with its match criteria replaced by the ones a test names, to give a policy a criterion the shipped
// schema lacks or a count of criteria it does not have (the planted-schema tests of SchemaIndexTests).
internal static class PlantedPolicySchema
{
    private static string SchemasDirectory => Path.Combine(AppContext.BaseDirectory, "schemas");

    private static (string Name, string Text) Shipped(string name) => ($"schemas/{name}", File.ReadAllText(Path.Combine(SchemasDirectory, name)));

    // The policy schema keeps its kind and runtime fields (scope and runtime roles); each of criteria is a string field comparing with its role.
    public static async Task<Binder> BinderAsync(params (string Field, string Role)[] criteria)
    {
        var (name, text) = Shipped("catalog/policy.schema.json");
        var schema = JObject.Parse(text);
        var match = (JObject)schema.SelectToken("properties.match.properties")!;
        match.Replace(new JObject(
            new JProperty("kind", match["kind"]),
            new JProperty("runtime", match["runtime"]),
            criteria.Select(c => new JProperty(c.Field, JObject.Parse($$"""{ "type": "string", "x-jd": { "match": "{{c.Role}}" } }""")))));

        return new Binder(await SchemaIndex.BuildAsync([Shipped("x-jd.schema.json"), Shipped("catalog/part.schema.json"), (name, schema.ToString())]));
    }
}
