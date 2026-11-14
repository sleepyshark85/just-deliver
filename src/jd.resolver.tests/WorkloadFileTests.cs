using jd.resolver.workload;
using Xunit;

namespace jd.resolver.tests;

public sealed class WorkloadFileTests : IDisposable
{
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "samples", "workload.yaml");

    private readonly string _temp = Directory.CreateTempSubdirectory("jd-workload-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private async Task<LoadResult<Workload>> LoadAsync(string content)
    {
        var path = Path.Combine(_temp, "workload.yaml");
        await File.WriteAllTextAsync(path, content);
        return await LoadFileAsync(path);
    }

    private static async Task<LoadResult<Workload>> LoadFileAsync(string path) =>
        await WorkloadFile.LoadAsync(new SourceDocument(path, await File.ReadAllBytesAsync(path)), CancellationToken.None);

    [Fact]
    public async Task The_sample_workload_loads()
    {
        var result = await LoadFileAsync(Sample);

        Assert.Empty(result.Errors);
        Assert.Equal("just-deliver-sample-app", result.Value?.Name);
    }

    [Fact]
    public async Task Invalid_yaml_is_located_in_the_file()
    {
        var error = Assert.Single((await LoadAsync("a: b\na: c\n")).Errors);

        Assert.Contains("duplicate key 'a' at line 2", error.Message);
    }

    [Fact]
    public async Task A_nested_schema_error_is_one_error_at_its_path()
    {
        var result = await LoadAsync((await File.ReadAllTextAsync(Sample)).Replace("port: 8080", "port: not-a-number"));

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("container.ports", error.Location);
        Assert.DoesNotContain('\n', error.ToString());
    }

    [Fact]
    public async Task A_rule_error_carries_its_location_and_message()
    {
        var result = await LoadAsync((await File.ReadAllTextAsync(Sample)) + "  - type: database\n");

        Assert.Null(result.Value);
        var error = Assert.Single(result.Errors);
        Assert.Equal("requires[0], requires[2]", error.Location);
        Assert.StartsWith("the id 'database' is used more than once", error.Message);
    }
}
