using Xunit;

namespace jd.cli.tests;

// Nothing set on the machine (docs/architecture/connections.md): a run whose environment holds only jd's data directory deploys, qualifies, approves and
// promotes, with every record in the data directory's store. The variables jd read before point elsewhere here, and nothing reaches them.
public sealed partial class ReleaseDeployCliTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deploy_qualify_approve_and_promote_keep_their_records_in_the_data_directory_and_the_removed_variables_have_no_effect(bool removedSet)
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_temp, "elsewhere")).FullName;
        if (removedSet)
        {
            foreach (var name in new[] { "JD_RECORDS_DIR", "PULUMI_CONFIG_PASSPHRASE_FILE", "JD_REGION" })
            {
                _variables[name] = elsewhere;
            }

            (_variables["PULUMI_BACKEND_URL"], _variables["PULUMI_CONFIG_PASSPHRASE"]) = ("file://" + elsewhere, "elsewhere");
        }

        var set = await CreateSetAsync();
        Act("object-1");
        var deployed = await RunAsync(new RecordingBackend(), Deploy(set));
        var qualified = await RunAsync(new RecordingBackend { Deployed = true }, Qualify(set));
        var stage = await StageAsync();
        Act(Alice);
        var approved = await ApproveAsync(stage);
        Act(Dave);
        var promoted = await PromoteAsync(stage, new RecordingBackend());

        Assert.True((deployed.Code, qualified.Code, approved.Code, promoted.Code) == (0, 0, 0, 0), deployed.Err + qualified.Err + approved.Err + promoted.Err);
        Assert.Equal(new[] { "deploy.json", "qualify.json", "set.yaml" }, Kinds(Path.Combine(_data, "records", "dev", "1.0.0")));
        Assert.Equal(new[] { "approve.json", "deploy.json", "set.yaml" }, Kinds(Path.Combine(_data, "records", "stage", "1.0.0")));
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    // What the files of a label are: set.yaml, or the activity of a <time>-<activity>.json record.
    private static string[] Kinds(string directory) =>
        Directory.GetFiles(directory).Select(file => Path.GetFileName(file)).Select(name => name == "set.yaml" ? name : name[(name.IndexOf('-', StringComparison.Ordinal) + 1)..]).Order(StringComparer.Ordinal).ToArray();
}
