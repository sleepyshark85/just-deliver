using jd.core.identity;
using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;
using jd.resolver.graph;

namespace jd.orchestrator.usecases;

/// <summary>
/// The checks the runs share, as use-case steps. The commands not yet behind a use case call them here until their own slices move them
/// (docs/architecture/application.md section 10).
/// </summary>
public static class Checks
{
    /// <summary>
    /// The environment's type must be the catalog's, and a type that requires an approval block must carry it (security boundary 13); the errors
    /// name <paramref name="file"/>.
    /// </summary>
    public static IReadOnlyList<LoadError> EnvironmentType(Catalog catalog, EnvironmentDescriptor environment, string file) =>
        EnvironmentTypeCheck.Check(catalog, environment, file);

    /// <summary>
    /// An environment whose descriptor carries an approval policy is deployed to only by promotion (docs/architecture/approvals.md section 2): the check
    /// reads the block, not the type. Run before the identity, the records or the backend is touched; a preview creates nothing, so it is not asked.
    /// </summary>
    public static Gated? Gate(EnvironmentDescriptor environment) => environment.Approval is null ? null : new Gated(environment.Name);

    /// <summary>
    /// The principal of the credential, named before any record is read or written; or, when the credential cannot name one, the refusal: exactly one of the
    /// two is null.
    /// </summary>
    public static async Task<(DeployerIdentity? Principal, IdentityRefused? Refusal)> PrincipalAsync(Connections connections, CancellationToken cancellationToken)
    {
        try
        {
            return (await connections.Identity().GetAsync(cancellationToken), null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, new IdentityRefused(ex.Message));
        }
    }

    /// <summary>
    /// The gated environment <paramref name="descriptor"/> describes, its policy and the principal, named before any record is read; or the descriptor's
    /// errors, or the refusal (<see cref="NotGated"/>, <see cref="IdentityRefused"/>). Approve's and promote's steps after their plain values.
    /// </summary>
    internal static async Task<(GatedTarget? Target, IReadOnlyList<LoadError> Errors, Problem? Refusal)> GatedTargetAsync(
        SourceDocument descriptor, Connections connections, CancellationToken cancellationToken)
    {
        var loaded = await EnvironmentParser.LoadAsync(descriptor, cancellationToken);
        if (loaded.Value is not { } environment)
        {
            return (null, loaded.Errors, null);
        }

        if (environment.Approval is not { } policy)
        {
            return (null, [], new NotGated(environment.Name));
        }

        var (principal, identityRefusal) = await PrincipalAsync(connections, cancellationToken);
        return principal is null ? (null, [], identityRefusal) : (new GatedTarget(environment, policy, principal), [], null);
    }

    /// <summary>The templates of <paramref name="catalog"/>'s templates part, and every way <paramref name="graphs"/> do not fit them, each once.</summary>
    public static (ITemplates Templates, IReadOnlyList<LoadError> Misfits) Templates(SourceCatalog catalog, IEnumerable<ResolvedGraph> graphs, Connections connections)
    {
        var templates = connections.Templates(CatalogLoader.Templates(catalog));
        return (templates, graphs.SelectMany(templates.Check).Distinct().ToList()); // unreadable templates are repeated by every graph's check
    }
}
