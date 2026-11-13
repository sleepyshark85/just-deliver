using System.Globalization;
using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary><c>release show</c>: reads a release set and verifies every embedded definition against its hash (docs/architecture/application.md section 3).</summary>
public static class ShowRelease
{
    /// <summary>Reads <paramref name="input"/>'s set; its errors name the document as the input does.</summary>
    /// <param name="input">The release set.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<ShowReleaseResult> RunAsync(ShowReleaseInput input, CancellationToken cancellationToken)
    {
        var loaded = await ReleaseSetFile.ParseAsync(input.Set, cancellationToken);
        return loaded.Set is { } set
            ? new ShowReleaseResult([], Outcome.Succeeded, set, set.CreatedAt.ToString(ReleaseRecordFile.DocumentTimeFormat, CultureInfo.InvariantCulture))
            : new ShowReleaseResult(loaded.Errors, Outcome.Failed, null, null);
    }
}
