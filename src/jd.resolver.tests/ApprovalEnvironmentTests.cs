using System.Text;
using jd.resolver.catalog;
using jd.resolver.environment;
using Xunit;

namespace jd.resolver.tests;

// The approval block in the descriptor and the definition (docs/architecture/approvals.md section 3): the schemas and the loader rules, and the
// catalog's type rules (docs/architecture/components.md section 2, boundary 13): a declared type, and the block on a type with approval: true.
public class ApprovalEnvironmentTests
{
    private const string Approver = "11111111-1111-1111-1111-111111111111";

    private const string Block = $"approval:\n  qualifiedOn: dev\n  required: 1\n  approvers: [\"{Approver}\"]\n  separateFromDeployer: true\n";

    private static string Descriptor(string type = "stage", string? approval = Block, string name = "stage") =>
        $"kind: Environment\nname: {name}\nregion: r1\ntype: {type}\noffers: []\nvalues: {{ a: b }}\n{approval}";

    private static string Definition(string type = "stage", string? approval = Block) =>
        $"kind: EnvironmentDefinition\nname: stage\nregion: region-1\ntype: {type}\noffers: [database]\n{approval}";

    private static Task<LoadResult<EnvironmentDescriptor>> LoadDefinitionAsync(string text) =>
        EnvironmentParser.LoadDefinitionAsync(new SourceDocument("definition.yaml", Encoding.UTF8.GetBytes(text)), CancellationToken.None);

    private static async Task<Catalog> SeedCatalogAsync() => (await SeedCatalog.LoadAsync()).Value!;

    [Fact]
    public async Task A_descriptor_carries_its_policy()
    {
        var result = await EnvironmentParser.ParseAsync("env.yaml", Descriptor());

        Assert.Empty(result.Errors);
        var policy = result.Value!.Approval!;
        Assert.Equal(("dev", 1, true), (policy.QualifiedOn, policy.Required, policy.SeparateFromDeployer));
        Assert.Equal([Approver], policy.Approvers);
    }

    // The schemas no longer couple type and block: the catalog does, so both files load either way and the check below decides.
    [Theory]
    [InlineData("dev", true)]
    [InlineData("dev", false)]
    [InlineData("stage", true)]
    [InlineData("stage", false)]
    public async Task The_schemas_load_any_type_with_or_without_a_policy(string type, bool withApproval)
    {
        var descriptor = await EnvironmentParser.ParseAsync("env.yaml", Descriptor(type, withApproval ? Block : string.Empty));
        var definition = await LoadDefinitionAsync(Definition(type, withApproval ? Block : string.Empty));

        Assert.Empty(descriptor.Errors);
        Assert.Empty(definition.Errors);
        Assert.Equal((type, withApproval), (descriptor.Value!.Type, descriptor.Value.Approval is not null));
        Assert.Equal((type, withApproval), (definition.Value!.Type, definition.Value.Approval is not null));
    }

    [Fact]
    public async Task A_type_that_is_not_a_type_name_is_a_schema_error_in_the_descriptor_and_the_definition()
    {
        var descriptor = await EnvironmentParser.ParseAsync("env.yaml", Descriptor("Stage"));
        var definition = await LoadDefinitionAsync(Definition("Stage"));

        Assert.Equal("type", Assert.Single(descriptor.Errors).Location);
        Assert.Equal("type", Assert.Single(definition.Errors).Location);
    }

    // As before types: any type may carry a block (the block is what gates); only a gated type must.
    [Theory]
    [InlineData("dev", true)]
    [InlineData("dev", false)]
    [InlineData("stage", true)]
    public async Task A_declared_type_passes_with_its_policy_and_an_ungated_type_may_carry_one(string type, bool withApproval)
    {
        var descriptor = (await EnvironmentParser.ParseAsync("env.yaml", Descriptor(type, withApproval ? Block : string.Empty))).Value!;

        Assert.Empty(EnvironmentTypeCheck.Check(await SeedCatalogAsync(), descriptor, "env.yaml"));
    }

    [Fact]
    public async Task A_gated_type_without_a_policy_is_an_error_in_the_descriptor_and_the_definition()
    {
        var catalog = await SeedCatalogAsync();
        var descriptor = (await EnvironmentParser.ParseAsync("env.yaml", Descriptor(approval: string.Empty))).Value!;
        var definition = (await LoadDefinitionAsync(Definition(approval: string.Empty))).Value!;

        foreach (var error in new[] { Assert.Single(EnvironmentTypeCheck.Check(catalog, descriptor, "env.yaml")), Assert.Single(EnvironmentTypeCheck.Check(catalog, definition, "env.yaml")) })
        {
            Assert.Equal(("env.yaml", "approval"), (error.File, error.Location));
            Assert.Equal("environment type 'stage' requires an 'approval' block; an environment of this type cannot be ungated.", error.Message);
        }
    }

    [Fact]
    public async Task An_undeclared_type_is_an_error_naming_the_type_and_the_declared_ones_even_with_a_policy()
    {
        var descriptor = (await EnvironmentParser.ParseAsync("env.yaml", Descriptor(type: "protected"))).Value!;

        var error = Assert.Single(EnvironmentTypeCheck.Check(await SeedCatalogAsync(), descriptor, "env.yaml"));

        Assert.Equal(("env.yaml", "type"), (error.File, error.Location));
        Assert.Equal("'protected' is not an environment type of the catalog (declared: dev, prod, shared, stage).", error.Message);
    }

    [Fact]
    public async Task An_environment_built_in_code_is_checked_with_an_empty_location()
    {
        var environment = new EnvironmentDescriptor("dev", "region-1", "protected", new Dictionary<string, string>(), [], null);

        var error = Assert.Single(EnvironmentTypeCheck.Check(await SeedCatalogAsync(), environment, "env.yaml"));

        Assert.Equal(("env.yaml", string.Empty), (error.File, error.Location));
        Assert.Equal("'protected' is not an environment type of the catalog (declared: dev, prod, shared, stage).", error.Message);
    }

    [Theory]
    [InlineData("required: 1", "required: 3", "approval.required")] // more than the one approver listed
    [InlineData("qualifiedOn: dev", "qualifiedOn: stage", "approval.qualifiedOn")] // its own name
    public async Task A_policy_that_cannot_be_met_or_names_its_own_environment_is_an_error_located_at_the_field(string original, string replacement, string location)
    {
        var result = await EnvironmentParser.ParseAsync("env.yaml", Descriptor(approval: Block.Replace(original, replacement, StringComparison.Ordinal)));

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("env.yaml", location), (error.File, error.Location));
    }

    // The definition is checked by the same loader as the descriptor (before S24c1, only when the composer re-read it).
    [Theory]
    [InlineData("required: 1", "required: 3", "approval.required", "3 approvers are required but only 1 are listed; the policy could never be met.")]
    [InlineData("qualifiedOn: dev", "qualifiedOn: stage", "approval.qualifiedOn", "'stage' cannot be qualified on itself; name the environment whose records must show the set qualified.")]
    public async Task A_policy_that_cannot_be_met_or_names_its_own_environment_is_reported_with_its_text_in_the_descriptor_and_the_definition(
        string original, string replacement, string location, string message)
    {
        var block = Block.Replace(original, replacement, StringComparison.Ordinal);

        var descriptor = await EnvironmentParser.ParseAsync("env.yaml", Descriptor(approval: block));
        var definition = await LoadDefinitionAsync(Definition(approval: block));

        var inDescriptor = Assert.Single(descriptor.Errors);
        var inDefinition = Assert.Single(definition.Errors);
        Assert.Equal(("env.yaml", location, message), (inDescriptor.File, inDescriptor.Location, inDescriptor.Message));
        Assert.Equal(("definition.yaml", location, message), (inDefinition.File, inDefinition.Location, inDefinition.Message));
        Assert.Null(definition.Value);
    }

    [Theory]
    [InlineData("required: 1", "required: 0", "approval.required")]
    [InlineData("required: 1", "required: 1.5", "approval.required")]
    [InlineData("qualifiedOn: dev", "qualifiedOn: Dev", "approval.qualifiedOn")]
    [InlineData("approvers: [\"11111111-1111-1111-1111-111111111111\"]", "approvers: []", "approval.approvers")]
    [InlineData("approvers: [\"11111111-1111-1111-1111-111111111111\"]", "approvers: [not-a-guid]", "approval.approvers[0]")]
    [InlineData("approvers: [\"11111111-1111-1111-1111-111111111111\"]", "approvers: [\"11111111-1111-1111-1111-111111111111\", \"11111111-1111-1111-1111-111111111111\"]", "approval.approvers")]
    [InlineData("  separateFromDeployer: true\n", "", "approval.separateFromDeployer")]
    [InlineData("separateFromDeployer: true", "separateFromDeployer: \"yes\"", "approval.separateFromDeployer")]
    [InlineData("  required: 1\n", "", "approval.required")]
    [InlineData("separateFromDeployer: true", "separateFromDeployer: true\n  roles: [x]", "approval.roles")]
    public async Task A_malformed_policy_is_a_schema_error_located_at_the_field_in_the_descriptor_and_the_definition(string original, string replacement, string location)
    {
        var block = Block.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(Block, block);

        var descriptor = await EnvironmentParser.ParseAsync("env.yaml", Descriptor(approval: block));
        var definition = await LoadDefinitionAsync(Definition(approval: block));

        Assert.Null(descriptor.Value);
        Assert.Null(definition.Value);
        Assert.Equal(location, Assert.Single(descriptor.Errors).Location);
        Assert.Equal(location, Assert.Single(definition.Errors).Location);
    }
}
