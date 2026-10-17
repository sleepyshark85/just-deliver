namespace jd.core.revisions;

/// <summary>A revision of a deployed runtime as the platform reports it: what serves (its traffic) and what it runs (its images).</summary>
/// <param name="Name">The revision's name.</param>
/// <param name="TrafficWeight">The share of traffic it gets, in percent; 0 when it holds none.</param>
/// <param name="Images">The image reference of each of its containers, as configured on the revision.</param>
public sealed record LiveRevision(string Name, int TrafficWeight, IReadOnlyList<string> Images);

/// <summary>
/// Reads the revisions of a deployed runtime from the platform itself, never from state or a record, so what runs can be checked against
/// what was pinned. The only input is the runtime's resource id.
/// </summary>
public interface ILiveRevisions
{
    /// <summary>Throws <see cref="InvalidOperationException"/>, fit to show a person, when the revisions cannot be read (the runtime is gone, access is refused, the platform cannot be reached).</summary>
    Task<IReadOnlyList<LiveRevision>> GetAsync(string resourceId, CancellationToken cancellationToken);
}
