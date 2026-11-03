using Xunit;

namespace jd.cli.tests;

// The local record store keeps every file inside its folder: each name is one path segment (IRecordStore.SegmentProblem), and a file is written once.
public sealed class LocalRecordStoreTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("jd-store-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Theory]
    [InlineData("dev", "1.0.0", "../x")]
    [InlineData("dev", "1.0.0", "a/b")]
    [InlineData("..", "1.0.0", "x")]
    [InlineData("dev", "..", "x")]
    [InlineData("../dev", "1.0.0", "x")]
    [InlineData("dev", "1.0.0/../../x", "x")]
    [InlineData("a\\b", "1.0.0", "x")]
    [InlineData("", "1.0.0", "x")]
    [InlineData("dev", "", "x")]
    [InlineData("dev", "1.0.0", "")]
    public async Task A_name_that_is_not_one_segment_is_refused_by_create_list_and_read_and_nothing_is_written(string environment, string label, string name)
    {
        var store = new LocalRecordStore(Path.Combine(_temp, "records"));
        await File.WriteAllTextAsync(Path.Combine(_temp, "x"), "outside");

        var created = await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(environment, label, name, new byte[] { 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(environment, label, name, CancellationToken.None));
        if (name == "x")
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.ListAsync(environment, label, CancellationToken.None));
        }

        Assert.EndsWith("cannot name a directory of the release records: it is empty or contains '..', '/' or '\\'.", created.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { Path.Combine(_temp, "x") }, Directory.GetFileSystemEntries(_temp, "*", SearchOption.AllDirectories));
        Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(_temp, "x")));
    }

    [Fact]
    public async Task An_existing_name_is_refused_and_keeps_its_bytes()
    {
        var store = new LocalRecordStore(Path.Combine(_temp, "records"));
        var path = await store.CreateAsync("dev", "1.0.0", "set.yaml", new byte[] { 1 }, CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync("dev", "1.0.0", "set.yaml", new byte[] { 2 }, CancellationToken.None));

        Assert.Equal(Path.Combine(_temp, "records", "dev", "1.0.0", "set.yaml"), path);
        Assert.Equal(new byte[] { 1 }, await store.ReadAsync("dev", "1.0.0", "set.yaml", CancellationToken.None));
        Assert.Equal(["set.yaml"], await store.ListAsync("dev", "1.0.0", CancellationToken.None));
    }
}
