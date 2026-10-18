using System.Text;
using Xunit;

namespace jd.cli.tests;

// jd's data directory (docs/architecture/connections.md): where it is, and the state's folder and key, created once and kept.
public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("jd-data-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_JD_DATA_DIR_it_is_jd_in_the_local_application_data_folder(string? unset) =>
        Assert.Equal(
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData, System.Environment.SpecialFolderOption.DoNotVerify), "jd"),
            DataDirectory.Resolve(name => name == "JD_DATA_DIR" ? unset : null).Root);

    [Fact]
    public void JD_DATA_DIR_overrides_it() =>
        Assert.Equal(_temp, DataDirectory.Resolve(name => name == "JD_DATA_DIR" ? _temp : null).Root);

    [Fact]
    public void The_state_folder_and_a_random_key_readable_by_its_owner_only_are_created_once_and_the_key_is_reused()
    {
        var data = DataDirectory.Resolve(name => name == "JD_DATA_DIR" ? Path.Combine(_temp, "data") : null);

        var (state, key) = data.PrepareState();
        var generated = File.ReadAllText(key);
        var again = data.PrepareState();

        Assert.Equal((Path.Combine(_temp, "data", "state"), Path.Combine(_temp, "data", "state.key")), (state, key));
        Assert.True(Directory.Exists(state));
        Assert.Equal(new[] { state, key }, Directory.GetFileSystemEntries(Path.Combine(_temp, "data")).Order(StringComparer.Ordinal)); // nothing left beside the key
        Assert.Equal((state, key), again);
        Assert.Equal(generated, File.ReadAllText(key));
        Assert.Equal(32, Convert.FromBase64String(generated).Length);
        Assert.NotEqual(generated, File.ReadAllText(DataDirectory.Resolve(name => name == "JD_DATA_DIR" ? Path.Combine(_temp, "other") : null).PrepareState().KeyFile));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
        }
    }

    [Fact]
    public async Task The_records_are_kept_in_its_records_folder()
    {
        var records = DataDirectory.Resolve(name => name == "JD_DATA_DIR" ? _temp : null).Records;

        var path = await records.CreateAsync("dev", "1.0.0", "set.yaml", Encoding.UTF8.GetBytes("the set"), CancellationToken.None);

        Assert.Equal(Path.Combine(_temp, "records", "dev", "1.0.0", "set.yaml"), path);
        Assert.Equal("the set", Encoding.UTF8.GetString(await records.ReadAsync("dev", "1.0.0", "set.yaml", CancellationToken.None)));
    }
}
