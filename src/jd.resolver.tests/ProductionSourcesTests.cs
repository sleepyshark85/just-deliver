using Xunit;

namespace jd.resolver.tests;

public sealed class ProductionSourcesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("jd-production-sources-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_production_sources_are_the_cs_files_outside_the_test_projects_and_the_build_output()
    {
        foreach (var file in new[] { "jd.a/Kept.cs", "jd.a/inner/Kept.cs", "jd.a/obj/Generated.cs", "jd.a/bin/Copied.cs", "jd.a.tests/Test.cs", "jd.a/notes.txt" })
        {
            var path = Path.Combine(_directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); // a path under the directory always has a parent
            File.WriteAllText(path, string.Empty);
        }

        Assert.Equal(["jd.a/Kept.cs", "jd.a/inner/Kept.cs"], LiteralGuardTests.ProductionSources(_directory).Order(StringComparer.Ordinal));
    }
}
