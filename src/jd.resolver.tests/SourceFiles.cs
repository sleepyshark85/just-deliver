namespace jd.resolver.tests;

// A file read as the command line reads one: its bytes, named by the path as given.
internal static class SourceFiles
{
    public static async Task<SourceDocument> ReadAsync(string path) => new(path, await File.ReadAllBytesAsync(path));
}
