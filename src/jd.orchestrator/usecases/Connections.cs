using jd.core.bp;
using jd.core.identity;
using jd.core.records;
using jd.core.revisions;

namespace jd.orchestrator.usecases;

/// <summary>
/// What a run reaches outside jd through, built once by a host's composition root (docs/architecture/application.md section 5). Each member is a
/// factory, invoked at the step that needs what it builds, so a run that stops earlier, or a preview, builds nothing it does not reach.
/// </summary>
/// <param name="Records">The release, qualification and approval records.</param>
/// <param name="Identity">Names the principal a run authenticates as.</param>
/// <param name="LiveRevisions">Reads what runs, with the identity's credential.</param>
/// <param name="Backend">Previews, deploys and reads the stacks.</param>
/// <param name="Probe">Checks that a new revision is healthy.</param>
/// <param name="Time">Stamps the records and times the run.</param>
public sealed record Connections(
    Func<IRecordStore> Records,
    Func<IDeployerIdentity> Identity,
    Func<ILiveRevisions> LiveRevisions,
    Func<IBackEndProvider> Backend,
    Func<IRevisionProbe> Probe,
    Func<TimeProvider> Time);
