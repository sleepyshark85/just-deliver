using Xunit;

namespace jd.bp.pulumi.tests;

public sealed class WorkDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jd-workdir-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task The_directory_is_per_stack_and_removed_after_success()
    {
        string? seen = null;

        var result = await WorkDirectory.RunAsync(_root, "shop.dev.orders.database", dir =>
        {
            seen = dir;
            File.WriteAllText(Path.Combine(dir, "Pulumi.yaml"), "name: x");
            return Task.FromResult(42);
        });

        Assert.Equal(42, result);
        Assert.Equal(Path.Combine(_root, "just-deliver", "shop.dev.orders.database"), seen);
        Assert.False(Directory.Exists(seen));
    }

    [Fact]
    public async Task The_directory_is_removed_after_failure()
    {
        string? seen = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkDirectory.RunAsync<int>(_root, "s", dir =>
        {
            seen = dir;
            File.WriteAllText(Path.Combine(dir, "Pulumi.yaml"), "name: x");
            throw new InvalidOperationException("boom");
        }));

        Assert.False(Directory.Exists(seen));
    }

    [Fact]
    public async Task A_directory_left_by_a_killed_process_is_cleared_first()
    {
        var stale = Path.Combine(_root, "just-deliver", "s");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "old.txt"), "stale");

        await WorkDirectory.RunAsync(_root, "s", dir =>
        {
            Assert.Empty(Directory.GetFileSystemEntries(dir));
            return Task.FromResult(0);
        });
    }
}
