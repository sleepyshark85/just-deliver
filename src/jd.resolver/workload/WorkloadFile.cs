using jd.definitionvalidator;
using Newtonsoft.Json.Linq;

namespace jd.resolver.workload;

/// <summary>
/// Reads a workload definition and validates it against the embedded workload schema and rules. The resolver relies on
/// a validated workload (node and stack names are unique by construction only then), so callers load through here.
/// </summary>
public static class WorkloadFile
{
    // Bridge: the kind read here until the workload is bound from its schema (S24c2).
    private const string Kind = "Workload";

    /// <summary>Loads the definition <paramref name="workload"/> holds; its errors name the document as it is named. The value is set exactly when there are no errors.</summary>
    public static async Task<LoadResult<JObject>> LoadAsync(SourceDocument workload, CancellationToken cancellationToken) =>
        Checked(await DocumentLoader.LoadAsync(workload.Name, workload.Text(), [Kind], cancellationToken));

    /// <summary>Validates workload text; <paramref name="source"/> only names the source in errors. Pure: the release set validates the definitions it embeds through here.</summary>
    public static async Task<LoadResult<JObject>> ParseAsync(string source, string text, CancellationToken cancellationToken = default) =>
        Checked(await DocumentLoader.LoadAsync(source, text, [Kind], cancellationToken));

    private static LoadResult<JObject> Checked(LoadResult<Document> loaded)
    {
        if (loaded.Value is not { Source: var source, Body: var workload })
        {
            return new LoadResult<JObject>(null, loaded.Errors);
        }

        var ruleErrors = WorkloadRules.Check(workload).Select(e => new LoadError(source, e.Location, e.Message)).ToList();
        return new LoadResult<JObject>(ruleErrors.Count == 0 ? workload : null, ruleErrors);
    }
}
