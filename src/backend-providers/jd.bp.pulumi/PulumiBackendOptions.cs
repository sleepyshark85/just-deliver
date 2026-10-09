using System.Reflection;

namespace jd.bp.pulumi;

public class PulumiBackendOptions
{
    [EnvironmentVariable("PULUMI_BACKEND_URL")]
    public string BackendUrl { get; set; } = "file://~";

    [EnvironmentVariable("PULUMI_CONFIG_PASSPHRASE")]
    public string ConfigPassPhrase { get; set; } = string.Empty;

    [EnvironmentVariable("PULUMI_HOME")]
    public string? PulumiHome { get; set; }

    public string ScratchDirectory { get; set; } = Path.GetTempPath();

    internal IDictionary<string, string?> GetEnvironmentVariables()
    {
        var result = new Dictionary<string, string?>();

        foreach (var property in GetType().GetProperties())
        {
            var attribute = property.GetCustomAttribute<EnvironmentVariableAttribute>();
            if (attribute is null)
            {
                continue;
            }

            if (property.GetValue(this) is string value)
            {
                result[attribute.Name] = value;
            }
        }

        return result;
    }
}
