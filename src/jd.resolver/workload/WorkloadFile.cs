namespace jd.resolver.workload;

/// <summary>
/// Reads a workload definition: validates it against the schema of its kind, binds it (<see cref="Binder.ReadWorkload"/>) and checks the
/// <see cref="WorkloadRules"/>. The resolver relies on a validated workload (node and stack names are unique by construction only then), so callers
/// load through here.
/// </summary>
public static class WorkloadFile
{
    /// <summary>Loads the definition <paramref name="workload"/> holds; its errors name the document as it is named. The value is set exactly when there are no errors.</summary>
    public static Task<LoadResult<Workload>> LoadAsync(SourceDocument workload, CancellationToken cancellationToken) =>
        ParseAsync(workload.Name, workload.Text(), cancellationToken);

    /// <summary>Validates workload text; <paramref name="source"/> only names the source in errors. Pure: the release set validates the definitions it embeds through here.</summary>
    public static async Task<LoadResult<Workload>> ParseAsync(string source, string text, CancellationToken cancellationToken = default)
    {
        // A workload is a document whose kind binds the workload's name; binding completeness holds for every kind that does.
        var index = await SchemaIndex.Shipped;
        index.CheckBindings();
        var loaded = await DocumentLoader.LoadAsync(source, text, index.KindsBinding(Roles.WorkloadName), cancellationToken);
        if (loaded.Value is not { } document)
        {
            return new LoadResult<Workload>(null, loaded.Errors);
        }

        var workload = new Binder(index).ReadWorkload(document);
        var errors = WorkloadRules.Check(workload, document.Source);
        return new LoadResult<Workload>(errors.Count == 0 ? workload : null, errors);
    }
}
