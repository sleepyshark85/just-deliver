using System.Reflection;

namespace jd.bp.pulumi;

/// <summary>How the Pulumi backend runs: where state is kept, the key it is encrypted with, and the directories the Pulumi CLI uses.</summary>
public sealed class PulumiBackendOptions
{
    // Pulumi reads a passphrase before a passphrase file; jd's state is encrypted with the file, so a passphrase in jd's own environment is removed.
    private const string PassphraseVariable = "PULUMI_CONFIG_PASSPHRASE";

    /// <summary>Where the stacks' state is kept (a Pulumi backend URL).</summary>
    [EnvironmentVariable("PULUMI_BACKEND_URL")]
    public string? BackendUrl { get; set; }

    /// <summary>The file holding the passphrase the state's secrets are encrypted with.</summary>
    [EnvironmentVariable("PULUMI_CONFIG_PASSPHRASE_FILE")]
    public string? ConfigPassPhraseFile { get; set; }

    /// <summary>The Pulumi CLI's home (plugins, credentials); null for the CLI's own default.</summary>
    [EnvironmentVariable("PULUMI_HOME")]
    public string? PulumiHome { get; set; }

    /// <summary>Where each stack operation gets its temporary working directory, holding the template it deploys.</summary>
    public string ScratchDirectory { get; set; } = Path.GetTempPath();

    // Every variable the options name is set as they say; a null value removes it from the Pulumi process, so a value in jd's own environment never applies.
    internal IDictionary<string, string?> GetEnvironmentVariables()
    {
        var result = new Dictionary<string, string?> { [PassphraseVariable] = null };

        foreach (var property in GetType().GetProperties())
        {
            var attribute = property.GetCustomAttribute<EnvironmentVariableAttribute>();
            if (attribute is null)
            {
                continue;
            }

            result[attribute.Name] = (string?)property.GetValue(this);
        }

        return result;
    }
}
