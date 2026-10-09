namespace jd.definitionvalidator;

/// <summary>The workload JSON Schema embedded in this assembly (see jd.definitionvalidator.csproj); the format ships with the code.</summary>
public static class WorkloadSchema
{
    public static async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = typeof(WorkloadSchema).Assembly.GetManifestResourceStream("schemas/workload.schema.json")
            ?? throw new InvalidOperationException("The workload schema is not embedded in jd.definitionvalidator.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
