using Xunit;

namespace jd.bp.pulumi.tests;

// What the Pulumi process is given: the options' values, and nothing of jd's own environment for the variables they name.
public sealed class PulumiBackendOptionsTests
{
    [Fact]
    public void The_state_settings_are_set_and_a_passphrase_or_unset_option_removes_the_variable_from_the_pulumi_process()
    {
        var variables = new PulumiBackendOptions { BackendUrl = "file:///data/state", ConfigPassPhraseFile = "/data/state.key" }.GetEnvironmentVariables();

        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["PULUMI_BACKEND_URL"] = "file:///data/state",
                ["PULUMI_CONFIG_PASSPHRASE_FILE"] = "/data/state.key",
                ["PULUMI_CONFIG_PASSPHRASE"] = null,
                ["PULUMI_HOME"] = null,
            },
            variables);
    }
}
