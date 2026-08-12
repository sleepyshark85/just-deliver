namespace jd.bp.pulumi.execution;

/// <summary>
/// Where Pulumi definitions are read from. An abstraction on purpose: DeploymentPackage carries
/// definition content as a string so the provider never learns where it came from, and the
/// executor must not reintroduce that coupling by reaching for the filesystem directly.
/// </summary>
public interface IDefinitionStore
{
    /// <summary>The definition's Pulumi.yaml. Throws when the definition does not exist.</summary>
    Task<string> GetProgramAsync(string definition);

    /// <summary>The definition's Pulumi.default.yaml, or null when it has none.</summary>
    Task<string?> GetDefaultParametersAsync(string definition);
}

public class FileSystemDefinitionStore : IDefinitionStore
{
    private readonly string _root;

    public FileSystemDefinitionStore(string root)
    {
        _root = root;
    }

    public async Task<string> GetProgramAsync(string definition)
    {
        var path = Path.Combine(_root, definition, "Pulumi.yaml");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Definition '{definition}' not found at '{path}'.", path);
        }

        return await File.ReadAllTextAsync(path);
    }

    public async Task<string?> GetDefaultParametersAsync(string definition)
    {
        var path = Path.Combine(_root, definition, "Pulumi.default.yaml");
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
    }
}
