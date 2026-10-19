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

    /// <summary>Loads the definition at <paramref name="path"/>; the value is set exactly when there are no errors.</summary>
    public static async Task<LoadResult<JObject>> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Checked(await DocumentLoader.LoadFileAsync(path, "workload definition", [Kind], cancellationToken));

    /// <summary>Validates workload text; <paramref name="path"/> only names the source in errors. Pure: the release set validates the definitions it embeds through here.</summary>
    public static async Task<LoadResult<JObject>> ParseAsync(string path, string text, CancellationToken cancellationToken = default) =>
        Checked(await DocumentLoader.LoadAsync(path, text, [Kind], cancellationToken));

    private static LoadResult<JObject> Checked(LoadResult<Document> loaded)
    {
        if (loaded.Value is not { Source: var path, Body: var workload })
        {
            return new LoadResult<JObject>(null, loaded.Errors);
        }

        // Rule errors read "<location>: <message>".
        var ruleErrors = WorkloadRules.Check(workload).Select(e =>
        {
            var split = e.IndexOf(": ", StringComparison.Ordinal);
            return split < 0 ? new LoadError(path, string.Empty, e) : new LoadError(path, e[..split], e[(split + 2)..]);
        }).ToList();
        return new LoadResult<JObject>(ruleErrors.Count == 0 ? workload : null, ruleErrors);
    }
}
