using jd.resolver.release;
using Xunit;

namespace jd.resolver.tests;

// The local record store keeps every file inside its folder: a name is one path segment.
public sealed class LocalRecordStoreTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("jd-store-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    public async Task A_name_that_is_not_one_segment_is_refused_by_create_and_read_and_nothing_is_written(string name)
    {
        var store = new LocalRecordStore(Path.Combine(_temp, "records"));
        await File.WriteAllTextAsync(Path.Combine(_temp, "x"), "outside");

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync("dev", "1.0.0", name, new byte[] { 1 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync("dev", "1.0.0", name, CancellationToken.None));

        Assert.Equal(new[] { Path.Combine(_temp, "x") }, Directory.GetFileSystemEntries(_temp, "*", SearchOption.AllDirectories));
        Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(_temp, "x")));
    }
}
