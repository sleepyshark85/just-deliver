using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary><c>release create</c>: builds an immutable release set from a manifest and the definitions it names (docs/architecture/application.md section 3).</summary>
public static class CreateRelease
{
    /// <summary>Builds the set <paramref name="input"/>'s manifest describes, stamped with the time of <paramref name="connections"/>.</summary>
    /// <param name="input">The manifest and the reader of the definitions it names.</param>
    /// <param name="connections">The clock the set is created at.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    public static async Task<CreateReleaseResult> RunAsync(CreateReleaseInput input, Connections connections, CancellationToken cancellationToken)
    {
        var built = await ReleaseBuilder.BuildAsync(input.Manifest, input.Definitions, connections.Time().GetUtcNow(), cancellationToken);
        return built.Set is { } set
            ? new CreateReleaseResult([], Outcome.Succeeded, set, ReleaseSetFile.Serialize(set))
            : new CreateReleaseResult(built.Errors, Outcome.Failed, null, null);
    }
}
