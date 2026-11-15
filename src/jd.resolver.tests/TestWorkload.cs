using jd.definitionvalidator;
using jd.resolver.workload;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// A workload bound as the loader binds one that passed its schema, without the schema, so a test writes only the fields it reads.
internal static class TestWorkload
{
    public static async Task<Workload> ParseAsync(string yaml)
    {
        Assert.True(YamlSchemaValidator.TryParseYaml(yaml, out var tree, out var error), error);
        return new Binder(await SchemaIndex.Shipped).ReadWorkload(new Document("workload.yaml", "Workload", (JObject)tree));
    }
}
