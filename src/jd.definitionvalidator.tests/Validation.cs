using Xunit;

namespace jd.definitionvalidator.tests;

// What the tests validate: YAML parsed as the loaders parse it (it must parse), then the tree against the schema.
internal static class Validation
{
    public static async Task<ValidationResult> ValidateYamlAsync(string schema, string yaml)
    {
        Assert.True(YamlSchemaValidator.TryParseYaml(yaml, out var tree, out var error), error);
        return await new YamlSchemaValidator().ValidateTokenAsync(schema, tree);
    }
}
