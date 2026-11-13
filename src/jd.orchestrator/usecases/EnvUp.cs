using System.Text;
using jd.resolver;
using jd.resolver.catalog;
using jd.resolver.environment;

namespace jd.orchestrator.usecases;

/// <summary>
/// <c>env up</c>: provisions what an environment definition's offers reach in its type's layer, then composes the environment's descriptor from
/// the outputs (docs/architecture/application.md section 3). Everything that can be known before anything is created is checked first.
/// </summary>
public static class EnvUp
{
    /// <summary>Runs <paramref name="input"/>; the backend is built only after the types, the graph, the descriptor and the templates are checked.</summary>
    /// <param name="input">The definition, the catalog and the base descriptor.</param>
    /// <param name="connections">The backend, the probe and the templates.</param>
    /// <param name="progress">Told as each node of the walk finishes.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<EnvUpResult> RunAsync(EnvUpInput input, Connections connections, Action<Progress>? progress, CancellationToken cancellationToken)
    {
        var definitionResult = await EnvironmentDefinitionFile.LoadAsync(input.Definition, cancellationToken);
        var catalogResult = await CatalogParser.ParseAsync(input.Catalog, cancellationToken);
        var baseResult = input.Base is null ? null : await EnvironmentParser.LoadAsync(input.Base, cancellationToken);
        if (definitionResult.Value is not { } definition || catalogResult.Value is not { } catalog || baseResult is { Value: null })
        {
            return Failed([.. definitionResult.Errors, .. input.Catalog.Located(catalogResult.Errors), .. baseResult?.Errors ?? []]);
        }

        // The definition's type, and the base's, must be the catalog's; a gated type's environment cannot be written without its block.
        var baseDescriptor = baseResult?.Value;
        var environment = definition.Over(baseDescriptor);
        var typeErrors = Checks.EnvironmentType(catalog, environment, input.Definition.Name).ToList();
        if (input.Base is { } baseDocument && baseDescriptor is not null)
        {
            typeErrors.AddRange(Checks.EnvironmentType(catalog, baseDescriptor, baseDocument.Name));
        }

        if (typeErrors.Count > 0)
        {
            return Failed(typeErrors);
        }

        var graph = Resolver.ResolveSubstrate(definition, input.Definition.Name, catalog, environment, baseDescriptor);
        if (graph.Errors.Count > 0)
        {
            return Failed(graph.Errors);
        }

        // The descriptor with its values still pending: what it can tell before the walk.
        var composer = new DescriptorComposer(definition, input.Definition.Name, catalog, environment, graph);
        var dryRun = await composer.ComposeAsync(null, cancellationToken);
        if (dryRun.Errors.Count > 0)
        {
            return Failed(dryRun.Errors);
        }

        var (orchestrator, misfits) = Walk.Prepare(input.Catalog, graph, catalog, environment, connections);
        if (orchestrator is null)
        {
            return Failed(misfits);
        }

        var run = await orchestrator.DeployAsync(graph, Walk.Reporting(progress), cancellationToken);
        if (!run.Succeeded)
        {
            return new EnvUpResult([], Outcome.Failed, run, environment, null);
        }

        // The bytes carry no byte-order mark: GetBytes writes none, as a StreamWriter writing a file writes none.
        var composed = await composer.ComposeAsync(run.Nodes.ToDictionary(n => n.NodeId, n => n.Outputs), cancellationToken);
        return composed.Yaml is { } yaml
            ? new EnvUpResult([], Outcome.Succeeded, run, environment, Encoding.UTF8.GetBytes(yaml))
            : new EnvUpResult(composed.Errors, Outcome.Failed, run, environment, null);
    }

    private static EnvUpResult Failed(IReadOnlyList<LoadError> errors) => new(errors, Outcome.Failed, null, null, null);
}
