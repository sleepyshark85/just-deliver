using Xunit;

namespace jd.cli.tests;

// A descriptor with an approval block is gated (docs/architecture/approvals.md section 2): the refusal comes before the principal, the records
// directory or the backend is touched. The block is what gates, not the tier, so the gated descriptor here is a `team` one.
public sealed partial class ReleaseDeployCliTests
{
    private const string ApprovalBlock = "approval:\n  qualifiedOn: build\n  required: 1\n  approvers: [\"00000000-0000-0000-0000-000000000000\"]\n  separateFromDeployer: true\n";

    private async Task<string> GatedEnvironmentAsync()
    {
        var path = Path.Combine(_temp, "gated.env.yaml");
        await File.WriteAllTextAsync(path, await File.ReadAllTextAsync(Environment) + ApprovalBlock);
        return path;
    }

    private static string[] Command(string command, string set, string environment, params string[] more) => command switch
    {
        "release deploy" => ["release", "deploy", set, "--env", environment, "--catalog", Catalog, .. more],
        "release qualify" => ["release", "qualify", set, "--env", environment, "--catalog", Catalog, .. more],
        _ => ["deploy", Path.Combine(Samples, "workloads", "sample-app.yaml"), "--env", environment, "--catalog", Catalog, .. more],
    };

    [Theory]
    [InlineData("release deploy")]
    [InlineData("release qualify")]
    [InlineData("deploy")]
    public async Task A_gated_environment_refuses_a_direct_deploy_before_any_identity_backend_record_or_set_yaml(string command)
    {
        var set = await CreateSetAsync();
        var gated = await GatedEnvironmentAsync();
        var backend = new RecordingBackend { Deployed = true };

        var (code, stdout, stderr) = await RunAsync(backend, Command(command, set, gated));

        Assert.Equal(1, code);
        Assert.Contains($"jd: {command}: environment 'dev' is gated by an approval policy: use `jd promote`.", stderr);
        Assert.Empty(stdout);
        Assert.Empty(backend.Calls);
        Assert.Equal(0, _identity.Calls);
        Assert.Empty(_live.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }

    [Theory]
    [InlineData("release deploy")]
    [InlineData("deploy")]
    public async Task A_preview_of_a_gated_environment_is_not_refused(string command)
    {
        var set = await CreateSetAsync();
        var gated = await GatedEnvironmentAsync();
        var backend = new RecordingBackend();

        var (code, _, stderr) = await RunAsync(backend, Command(command, set, gated, "--preview"));

        Assert.Equal(0, code);
        Assert.Empty(stderr);
        Assert.NotEmpty(backend.Calls);
        Assert.All(backend.Calls, call => Assert.StartsWith("preview ", call));
        Assert.Empty(Directory.GetFileSystemEntries(_records));
    }
}
