using System.Diagnostics;
using jd.core.bp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace jd.bp.pulumi.tests;

/// <summary>
/// Drives the real Pulumi CLI against the local file backend in a temp directory, with a template that has
/// no resources, so no provider, cloud or network is involved. Fails (does not skip) without the CLI.
/// </summary>
[Trait("Category", "Pulumi")]
public sealed class PulumiProviderTests : IDisposable
{
    private const string Template = """
        name: echo
        runtime: yaml
        configuration:
          greeting:
            type: String
          password:
            type: String
        outputs:
          greeting: ${greeting}
          password: ${password}
        """;

    private const string Stack = "shop.dev.orders.echo";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jd-pulumi-" + Guid.NewGuid().ToString("N"));
    private readonly IBackEndProvider _provider;

    public PulumiProviderTests()
    {
        RequirePulumiCli();

        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterPulumiBackend(options =>
        {
            options.BackendUrl = "file://" + Path.Combine(_root, "state");
            options.ConfigPassPhrase = "test-passphrase";
            options.ScratchDirectory = Path.Combine(_root, "scratch");
        });
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        _provider = services.BuildServiceProvider().GetRequiredService<IBackEndProvider>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void RequirePulumiCli()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("pulumi", "version") { RedirectStandardOutput = true })
                ?? throw new InvalidOperationException("could not start");
            process.WaitForExit();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException(
                "The Pulumi tests need the 'pulumi' CLI on PATH (https://www.pulumi.com/docs/install/); CI installs it with pulumi/actions. Install it or exclude Category=Pulumi.", e);
        }
    }

    private static DeploymentPackage Package(string greeting = "hello", string stack = Stack) => new()
    {
        StackName = stack,
        DeploymentContent = Template,
        DeploymentParameters = new()
        {
            ["greeting"] = new ConfigEntry(greeting),
            ["password"] = new ConfigEntry("s3cret-value", isSecret: true),
        },
    };

    private string ScratchStackDirectory(string stack) => Path.Combine(_root, "scratch", "just-deliver", stack);

    [Fact]
    public async Task Preview_deploy_read_outputs_and_redeploy_without_changes()
    {
        var ct = CancellationToken.None;

        Assert.Null(await _provider.GetOutputsAsync(Package(), ct));

        var preview = await _provider.PreviewAsync(Package(), ct);
        Assert.Empty(preview.Outputs);
        Assert.True(preview.Summary.GetValueOrDefault("Create") > 0);
        Assert.Null(await _provider.GetOutputsAsync(Package(), ct)); // the preview created an empty stack: still not deployed

        var deployed = await _provider.DeployAsync(Package(), ct);
        Assert.Equal("hello", deployed.Outputs["greeting"].Value);
        Assert.False(deployed.Outputs["greeting"].IsSecret);

        var read = await _provider.GetOutputsAsync(Package(), ct);
        Assert.NotNull(read);
        Assert.Equal("hello", read["greeting"].Value);

        var again = await _provider.DeployAsync(Package(), ct);
        Assert.Empty(again.Changes);
        Assert.DoesNotContain(again.Summary, kvp => kvp.Key != "Same" && kvp.Value > 0);
        Assert.Equal("hello", again.Outputs["greeting"].Value);
    }

    [Fact]
    public async Task A_secret_config_value_arrives_as_a_secret_output_and_is_not_stored_in_clear()
    {
        var ct = CancellationToken.None;

        var deployed = await _provider.DeployAsync(Package(), ct);
        var read = await _provider.GetOutputsAsync(Package(), ct);

        Assert.True(deployed.Outputs["password"].IsSecret);
        Assert.Equal("s3cret-value", deployed.Outputs["password"].Value);
        Assert.NotNull(read);
        Assert.True(read["password"].IsSecret);
        Assert.Equal("s3cret-value", read["password"].Value);
        // Positive control: the scan reads real state, in which the non-secret value is visible in clear.
        var state = Directory.EnumerateFiles(Path.Combine(_root, "state"), "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(state);
        var contents = await Task.WhenAll(state.Select(file => File.ReadAllTextAsync(file, ct)));
        Assert.Contains(contents, text => text.Contains("hello"));
        Assert.DoesNotContain(contents, text => text.Contains("s3cret-value"));
    }

    [Fact]
    public async Task Stacks_of_one_template_are_independent()
    {
        var ct = CancellationToken.None;

        await _provider.DeployAsync(Package("one", "shop.dev.a.echo"), ct);
        await _provider.DeployAsync(Package("two", "shop.dev.b.echo"), ct);

        Assert.Equal("one", (await _provider.GetOutputsAsync(Package(stack: "shop.dev.a.echo"), ct))!["greeting"].Value);
        Assert.Equal("two", (await _provider.GetOutputsAsync(Package(stack: "shop.dev.b.echo"), ct))!["greeting"].Value);
    }

    [Fact]
    public async Task A_stack_name_over_the_limit_deploys_and_is_read_back_by_its_graph_name()
    {
        var ct = CancellationToken.None;
        var longStack = "shop.dev." + new string('x', 120) + ".echo";

        await _provider.DeployAsync(Package(stack: longStack), ct);

        Assert.Equal("hello", (await _provider.GetOutputsAsync(Package(stack: longStack), ct))!["greeting"].Value);
    }

    [Fact]
    public async Task The_work_directory_is_removed_after_success_and_after_failure()
    {
        var ct = CancellationToken.None;

        await _provider.DeployAsync(Package(), ct);
        Assert.False(Directory.Exists(ScratchStackDirectory(Stack)));

        var broken = Package();
        broken.DeploymentContent = "name: echo\nruntime: yaml\nresources:\n  r:\n    type: not:a:Valid\n";
        await Assert.ThrowsAnyAsync<Exception>(() => _provider.PreviewAsync(broken, ct));
        Assert.False(Directory.Exists(ScratchStackDirectory(Stack)));

        await _provider.GetOutputsAsync(Package(), ct);
        Assert.False(Directory.Exists(ScratchStackDirectory(Stack)));
    }

    [Fact]
    public async Task A_cancelled_token_stops_every_operation_and_leaves_no_work_directory()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.DeployAsync(Package(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.PreviewAsync(Package(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.GetOutputsAsync(Package(), cts.Token));

        Assert.False(Directory.Exists(ScratchStackDirectory(Stack)));
    }

    [Fact]
    public async Task An_invalid_stack_name_fails_the_returned_task()
    {
        var task = _provider.DeployAsync(Package(stack: "../escape"), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => task);
    }
}
