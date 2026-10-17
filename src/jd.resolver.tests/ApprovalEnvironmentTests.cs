using jd.resolver.environment;
using Xunit;

namespace jd.resolver.tests;

// The approval block in the descriptor and the definition (docs/architecture/approvals.md section 3): the schemas, the tier coupling in their anyOf form, and the loader rules.
public class ApprovalEnvironmentTests
{
    private const string Approver = "11111111-1111-1111-1111-111111111111";

    private const string Block = $"approval:\n  qualifiedOn: dev\n  required: 1\n  approvers: [\"{Approver}\"]\n  separateFromDeployer: true\n";

    private static string Descriptor(string tier = "protected", string? approval = Block, string name = "stage") =>
        $"kind: Environment\nname: {name}\nregion: r1\ntier: {tier}\nvalues: {{ a: b }}\n{approval}";

    private static string Definition(string tier = "protected", string? approval = Block) =>
        $"kind: EnvironmentDefinition\nname: stage\ntier: {tier}\nrequires: [{{ type: env-substrate }}]\nvalues: {{ a: b }}\n{approval}";

    private static async Task<EnvironmentDefinitionLoadResult> LoadDefinitionAsync(string text)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("jd-approval").FullName, "definition.yaml");
        await File.WriteAllTextAsync(path, text);
        return await EnvironmentDefinitionFile.LoadAsync(path);
    }

    [Fact]
    public async Task A_descriptor_carries_its_policy()
    {
        var result = await EnvironmentParser.ParseAsync("env.yaml", Descriptor());

        Assert.Empty(result.Errors);
        var policy = result.Descriptor!.Approval!;
        Assert.Equal(("dev", 1, true), (policy.QualifiedOn, policy.Required, policy.SeparateFromDeployer));
        Assert.Equal([Approver], policy.Approvers);
    }

    [Theory]
    [InlineData("team", true)]
    [InlineData("team", false)]
    [InlineData("protected", true)]
    public async Task A_team_environment_may_carry_a_policy_or_not_and_a_protected_one_must(string tier, bool withApproval)
    {
        var descriptor = await EnvironmentParser.ParseAsync("env.yaml", Descriptor(tier, withApproval ? Block : string.Empty));
        var definition = await LoadDefinitionAsync(Definition(tier, withApproval ? Block : string.Empty));

        Assert.Empty(descriptor.Errors);
        Assert.Empty(definition.Errors);
        Assert.Equal(withApproval, descriptor.Descriptor!.Approval is not null);
        Assert.Equal(withApproval, definition.Definition!.Approval is not null);
    }

    [Fact]
    public async Task A_protected_environment_without_a_policy_is_an_error_in_the_descriptor_and_the_definition()
    {
        var descriptor = await EnvironmentParser.ParseAsync("env.yaml", Descriptor(approval: string.Empty));
        var definition = await LoadDefinitionAsync(Definition(approval: string.Empty));

        Assert.Null(descriptor.Descriptor);
        Assert.Null(definition.Definition);
        Assert.NotEmpty(descriptor.Errors);
        Assert.NotEmpty(definition.Errors);
    }

    [Theory]
    [InlineData("required: 1", "required: 3", "approval.required")] // more than the one approver listed
    [InlineData("qualifiedOn: dev", "qualifiedOn: stage", "approval.qualifiedOn")] // its own name
    public async Task A_policy_that_cannot_be_met_or_names_its_own_environment_is_an_error_located_at_the_field(string original, string replacement, string location)
    {
        var result = await EnvironmentParser.ParseAsync("env.yaml", Descriptor(approval: Block.Replace(original, replacement, StringComparison.Ordinal)));

        Assert.Null(result.Descriptor);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("env.yaml", location), (error.File, error.Location));
    }

    [Theory]
    [InlineData("required: 1", "required: 0")]
    [InlineData("required: 1", "required: 1.5")]
    [InlineData("qualifiedOn: dev", "qualifiedOn: Dev")]
    [InlineData("approvers: [\"11111111-1111-1111-1111-111111111111\"]", "approvers: []")]
    [InlineData("approvers: [\"11111111-1111-1111-1111-111111111111\"]", "approvers: [not-a-guid]")]
    [InlineData("approvers: [\"11111111-1111-1111-1111-111111111111\"]", "approvers: [\"11111111-1111-1111-1111-111111111111\", \"11111111-1111-1111-1111-111111111111\"]")]
    [InlineData("  separateFromDeployer: true\n", "")]
    [InlineData("separateFromDeployer: true", "separateFromDeployer: \"yes\"")]
    [InlineData("  required: 1\n", "")]
    [InlineData("separateFromDeployer: true", "separateFromDeployer: true\n  roles: [x]")]
    public async Task A_malformed_policy_is_a_schema_error_in_the_descriptor_and_the_definition(string original, string replacement)
    {
        var block = Block.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(Block, block);

        var descriptor = await EnvironmentParser.ParseAsync("env.yaml", Descriptor(approval: block));
        var definition = await LoadDefinitionAsync(Definition(approval: block));

        Assert.Null(descriptor.Descriptor);
        Assert.Null(definition.Definition);
        Assert.NotEmpty(descriptor.Errors);
        Assert.NotEmpty(definition.Errors);
    }
}
