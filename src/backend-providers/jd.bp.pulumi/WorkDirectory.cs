namespace jd.bp.pulumi;

/// <summary>
/// A deterministic per-stack directory under the scratch root that exists only for the duration of one
/// operation. Pulumi keeps its state in the backend, so nothing here needs to survive the operation.
/// </summary>
internal static class WorkDirectory
{
    public static async Task<T> RunAsync<T>(string scratchRoot, string stackName, Func<string, Task<T>> operation)
    {
        var path = Path.Combine(scratchRoot, "just-deliver", stackName);

        // Left behind only if a previous process was killed mid-operation.
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        try
        {
            return await operation(path);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
