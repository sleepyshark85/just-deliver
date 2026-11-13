using jd.core.identity;
using jd.core.records;
using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;
using jd.resolver.release;

namespace jd.orchestrator.usecases;

/// <summary>
/// The start <c>release deploy</c> and <c>release qualify</c> share (docs/architecture/application.md sections 5 and 8), in this order: the set, the
/// catalog and the environment loaded; the environment's type checked; the gate; a promotion's kept set re-hashed; every workload resolved and checked
/// against the templates; for a recorded run the principal named, then the set kept; then the backend, the orchestrator and the qualifier built
/// together, before any backend call. The first step of <see cref="DeployRelease"/> and <see cref="QualifyRelease"/>.
/// </summary>
internal static class ReleaseStart
{
    // A run whose documents loaded and passed every check: the set and its exact bytes, the catalog, the environment, each workload's graph and the
    // templates they fit.
    private sealed record Checked(ReleaseSet Set, ReadOnlyMemory<byte> SetBytes, Catalog Catalog, EnvironmentDescriptor Environment, IReadOnlyList<ResolvedGraph> Graphs, ITemplates Templates);

    // What a recorded run has once its set is kept.
    private sealed record Kept(IRecordStore Records, DeployerIdentity Identity, string SetSha256);

    /// <summary>Starts the run of <paramref name="input"/>, or stops at the first refusal with nothing deployed.</summary>
    /// <param name="input">The set or the promotion, the environment, the catalog and whether to only preview.</param>
    /// <param name="connections">The records, the identity, the backend, the probe, the reader of live revisions, the clock and the templates.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <exception cref="ArgumentException">The input has both a set and a promotion, neither, or a promotion that previews.</exception>
    public static async Task<ReleaseStartResult> StartAsync(DeployReleaseInput input, Connections connections, CancellationToken cancellationToken)
    {
        if ((input.Set is null) == (input.Promotion is null) || (input.Promotion is not null && input.Preview))
        {
            throw new ArgumentException("a release run starts from a set or from a promotion, and a promotion is never a preview.", nameof(input));
        }

        // A preview deploys nothing, so it records nothing.
        var records = input.Preview ? null : connections.Records();
        var time = connections.Time();
        var started = time.GetUtcNow();
        var (run, errors, problem) = await CheckAsync(input, records, connections, cancellationToken);
        if (run is null)
        {
            return new(errors, problem, null);
        }

        // A recorded run knows who deploys, and that no other set was deployed under this label, before it calls the backend.
        Kept? kept = null;
        if (records is not null)
        {
            (kept, problem) = await KeepAsync(records, input.Promotion?.Principal, run, connections, cancellationToken);
            if (problem is not null)
            {
                return Refused(problem);
            }
        }

        return Connect(run, kept, connections, time, started);
    }

    // The set (a promotion's: the kept one), the catalog and the environment loaded and checked, every workload resolved and fitted to the templates;
    // or the errors, or the refusal, that stop the run.
    private static async Task<(Checked? Run, IReadOnlyList<LoadError> Errors, Problem? Problem)> CheckAsync(
        DeployReleaseInput input, IRecordStore? records, Connections connections, CancellationToken cancellationToken)
    {
        // StartAsync checked the input: a promotion has the records, and a run without one has its set.
        var set = input.Promotion is { } promoted ? await KeptSetAsync(records!, promoted, cancellationToken) : input.Set!;
        var setResult = await ReleaseSetFile.ParseAsync(set, cancellationToken);
        var catalogResult = await CatalogParser.ParseAsync(input.Catalog, cancellationToken);
        var environmentResult = await EnvironmentParser.LoadAsync(input.Environment, cancellationToken);
        if (setResult.Set is not { } release || catalogResult.Value is not { } catalog || environmentResult.Value is not { } environment)
        {
            return (null, [.. setResult.Errors, .. input.Catalog.Located(catalogResult.Errors), .. environmentResult.Errors], null);
        }

        if (Checks.EnvironmentType(catalog, environment, input.Environment.Name) is { Count: > 0 } typeErrors)
        {
            return (null, typeErrors, null);
        }

        // A gated environment is deployed to only by promotion; this stops before the identity, the records or the backend is touched.
        if (!input.Preview && input.Promotion is null && Checks.Gate(environment) is { } gated)
        {
            return (null, [], gated);
        }

        // The promotion judged the hash of the kept set; the bytes read now are the ones deployed, so they must be those.
        if (input.Promotion is { } promotion && ReleaseHash.Of(setResult.Bytes.Span) != promotion.SetSha256)
        {
            return (null, [], new KeptSetChanged(set.Name, promotion.SetSha256));
        }

        // Everything is resolved and checked before anything is created.
        var (graphs, errors) = await ReleaseResolver.ResolveAsync(set.Name, release, catalog, environment, cancellationToken);
        if (errors.Count > 0)
        {
            return (null, errors, null);
        }

        var (templates, misfits) = Checks.Templates(input.Catalog, graphs, connections);
        return misfits.Count > 0 ? (null, misfits, null) : (new Checked(release, setResult.Bytes, catalog, environment, graphs, templates), [], null);
    }

    private static ReleaseStartResult Refused(Problem problem) => new([], problem, null);

    // The set kept for the promoted label on the environment it was qualified on, named as the records name it.
    private static async Task<SourceDocument> KeptSetAsync(IRecordStore records, Promotion promotion, CancellationToken cancellationToken) =>
        new(ReleaseRecordFile.KeptSetName(promotion.QualifiedOn, promotion.Label), await records.ReadAsync(promotion.QualifiedOn, promotion.Label, ReleaseRecordFile.SetFileName, cancellationToken));

    // The principal (known already, for a promotion) and the kept set's hash; or why the run must not start: the principal cannot be named, the label
    // cannot name a place in the records, or it holds another set.
    private static async Task<(Kept? Kept, Problem? Refusal)> KeepAsync(
        IRecordStore records, DeployerIdentity? known, Checked run, Connections connections, CancellationToken cancellationToken)
    {
        var (principal, identityRefusal) = known is null ? await Checks.PrincipalAsync(connections, cancellationToken) : (known, null);
        if (principal is null)
        {
            return (null, identityRefusal);
        }

        try
        {
            var (sha256, conflict) = await ReleaseRecordFile.KeepSetAsync(records, run.Environment.Name, run.Set.Label, run.SetBytes, cancellationToken);
            return conflict is null ? (new Kept(records, principal, sha256), null) : (null, new RecordingRefused(conflict));
        }
        catch (ArgumentException ex)
        {
            return (null, new RecordingRefused(ex.Message));
        }
    }

    // Everything the run calls, built together before any backend call, so one that cannot be built stops the run before anything is deployed. A
    // preview records nothing and qualifies nothing, so it has no qualifier.
    private static ReleaseStartResult Connect(Checked run, Kept? kept, Connections connections, TimeProvider time, DateTimeOffset started)
    {
        try
        {
            var backend = connections.Backend();
            var orchestrator = new Orchestrator(backend, run.Templates, run.Catalog, run.Environment, connections.Probe());
            var recording = kept is null
                ? null
                : new Recording(kept.Records, kept.Identity, kept.SetSha256, new Qualifier(orchestrator, backend, run.Templates, connections.LiveRevisions(), time));
            return new([], null, new StartedRelease(run.Set, run.Environment, run.Graphs, orchestrator, recording, time, started));
        }
        catch (InvalidOperationException ex)
        {
            return Refused(new ConnectionFailed(ex.Message));
        }
    }
}
