using jd.resolver;
using jd.resolver.Models;

namespace jd.resolver.tests;

/// <summary>
/// Resolves against the repository's real mappings and definitions rather than fixtures,
/// so these tests fail when a mapping file drifts from the definition it binds to.
/// </summary>
public class RepositoryFixture
{
    public string RepositoryRoot { get; }
    public SharedSettings Shared { get; }
    public Dictionary<string, MappingDocument> Mappings { get; }
    public IDefinitionStore Definitions { get; }

    public RepositoryFixture()
    {
        RepositoryRoot = FindRepositoryRoot();
        var mappingsDirectory = Path.Combine(RepositoryRoot, "definitions", "mappings");

        (Shared, Mappings) = new MappingLoader().LoadAsync(mappingsDirectory).GetAwaiter().GetResult();
        Definitions = new FileSystemDefinitionStore(Path.Combine(RepositoryRoot, Shared.DefinitionsRoot));
    }

    public WorkloadResolver CreateResolver() => new(Shared, Mappings, Definitions);

    public WorkloadDocument SampleWorkload() =>
        MappingLoader.LoadWorkload(File.ReadAllText(
            Path.Combine(RepositoryRoot, "samples", "provisioner", "workload.yaml")));

    /// <summary>Walks up from the test binaries until the repository markers appear.</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "definitions", "mappings"))
                && Directory.Exists(Path.Combine(directory.FullName, "schemas")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
