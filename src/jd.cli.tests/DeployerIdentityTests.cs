using Xunit;

namespace jd.cli.tests;

public sealed class DeployerIdentityTests
{
    [Fact]
    public void The_composition_root_names_the_unset_variables_and_never_echoes_the_secret()
    {
        var variables = new Dictionary<string, string> { ["ARM_CLIENT_SECRET"] = "the-secret" };

        var error = Assert.Throws<InvalidOperationException>(() => Cli.CreateDeployerIdentity(name => variables.GetValueOrDefault(name)));

        Assert.Contains("ARM_TENANT_ID, ARM_CLIENT_ID", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ARM_CLIENT_SECRET", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("the-secret", error.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The deploying principal as Entra reports it, for the sandbox team identity the Azure tests run as. Creates no resource: it only
/// obtains a token. Run with <c>tools/verify.sh --azure</c>.
/// </summary>
[Trait("Category", "Azure")]
public sealed class DeployerIdentityAzureTests
{
    [Fact]
    public async Task The_identity_names_the_service_principal_of_the_arm_variables()
    {
        // Login refuses unless started by tools/verify.sh --azure and checks the ARM_* variables are set.
        using var az = AzureCli.Login();

        var identity = await Cli.CreateDeployerIdentity(System.Environment.GetEnvironmentVariable).GetAsync(CancellationToken.None);

        Assert.Equal(System.Environment.GetEnvironmentVariable("ARM_CLIENT_ID"), identity.ClientId);
        Assert.Equal(System.Environment.GetEnvironmentVariable("ARM_TENANT_ID"), identity.TenantId);
        Assert.False(string.IsNullOrEmpty(identity.ObjectId));
    }
}
