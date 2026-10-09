namespace jd.resolver;

/// <summary>Reads the JSON Schemas embedded in this assembly (see jd.resolver.csproj).</summary>
internal static class EmbeddedSchema
{
    public static async Task<string> ReadAsync(string name, CancellationToken cancellationToken)
    {
        await using var stream = typeof(EmbeddedSchema).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Schema '{name}' is not embedded in jd.resolver.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
