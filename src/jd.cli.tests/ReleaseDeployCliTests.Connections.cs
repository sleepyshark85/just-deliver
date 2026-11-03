using Xunit;

namespace jd.cli.tests;

// Nothing set on the machine (docs/architecture/connections.md): a run whose environment holds only jd's data directory deploys, qualifies, approves and
// promotes, with every record in the data directory's store. The variables jd read before point elsewhere here, and nothing reaches them.
// Each connection is built lazily, at the step that needs it (docs/architecture/application.md section 5): a run that stops earlier builds none of the later ones.
public sealed partial class ReleaseDeployCliTests
{
    [Fact]
    public async Task A_recorded_run_builds_the_records_then_the_identity_then_the_backend_probe_and_reader_together()
    {
        var set = await CreateSetAsync();

        var (code, _, stderr) = await RunAsync(new RecordingBackend(), Deploy(set));

        Assert.True(code == 0, stderr);
        Assert.Equal(["Records", "Time", "Identity", "Backend", "Probe", "LiveRevisions"], Invoked);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("validate")]
    [InlineData("release show")]
    public async Task A_command_that_deploys_nothing_builds_no_connection(string command)
    {
        var set = await CreateSetAsync();
        string[] args = command switch
        {
            "preview" => ["preview", Path.Combine(Samples, "workloads", "sample-app.yaml"), "--env", Environment, "--catalog", Catalog],
            "validate" => ["validate", Path.Combine(Samples, "workloads", "sample-app.yaml")],
            _ => ["release", "show", set],
        };

        var (code, _, stderr) = await RunAsync(new RecordingBackend(), args);

        Assert.True(code == 0, stderr);
        Assert.Empty(Invoked);
    }

    [Fact]
    public async Task A_release_preview_builds_no_records_identity_or_reader()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Deploy(set, "--preview"));

        Assert.True(code == 0, stderr);
        Assert.Equal(["Time", "Backend", "Probe"], Invoked);
        Assert.Equal(0, _identity.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    [Theory]
    [InlineData("release deploy")]
    [InlineData("deploy")]
    public async Task A_gated_refusal_builds_no_identity_backend_probe_or_reader(string command)
    {
        var set = await CreateSetAsync();

        var (code, _, _) = await RunAsync(new RecordingBackend(), Command(command, set, await GatedEnvironmentAsync()));

        Assert.Equal(1, code);
        Assert.Equal(command == "deploy" ? [] : ["Records", "Time"], Invoked);
    }

    [Fact]
    public async Task A_bad_identity_builds_no_backend_probe_or_reader_and_keeps_no_set()
    {
        var set = await CreateSetAsync();
        _identity.Failure = new InvalidOperationException("the credential was refused.");
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Deploy(set));

        Assert.Equal((1, "jd: the credential was refused.\n"), (code, stderr));
        Assert.Equal(["Records", "Time", "Identity"], Invoked);
        Assert.Empty(backend.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    // Risk c of application.md section 8: every reader is built before the first backend call, so one that cannot be built stops the run before anything is deployed.
    [Fact]
    public async Task A_reader_that_cannot_be_built_stops_the_run_before_any_backend_call()
    {
        var set = await CreateSetAsync();
        var backend = new RecordingBackend();

        var (code, stdout, stderr) = await RunWithAsync(Connections(backend) with { LiveRevisions = () => throw new InvalidOperationException("the reader cannot be built.") }, Deploy(set));

        Assert.Equal((1, "jd: the reader cannot be built.\n"), (code, stderr));
        Assert.Empty(stdout);
        Assert.Empty(backend.Calls);
        Assert.Equal(["Records", "Time", "Identity", "Backend", "Probe", "LiveRevisions"], Invoked);
    }

    [Fact]
    public async Task Approve_builds_nothing_for_an_environment_without_a_policy_and_no_records_for_a_bad_identity()
    {
        var set = await CreateSetAsync();
        var ungated = await RunAsync(null, "approve", "1.0.0", "--env", Environment, "--meaning", "ready");
        var ungatedInvoked = Invoked.ToList();
        Invoked.Clear();
        _identity.Failure = new InvalidOperationException("the credential was refused.");

        var refused = await ApproveAsync(await StageAsync());

        Assert.Equal((1, 1), (ungated.Code, refused.Code));
        Assert.Empty(ungatedInvoked);
        Assert.Equal(["Identity"], Invoked);
    }

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
