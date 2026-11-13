using jd.resolver;

namespace jd.orchestrator.usecases;

/// <summary>
/// <c>deploy</c>: resolves a workload, checks the graph against the catalog's templates, then walks it: provisions the infrastructure nodes in
/// order and releases the runtime, or with <see cref="DeployInput.Preview"/> only shows the changes (docs/architecture/application.md section 3).
/// </summary>
public static class Deploy
{
    /// <summary>Runs <paramref name="input"/>; everything that can be checked is checked before the backend is built.</summary>
    /// <param name="input">The workload, the environment, the catalog and whether to only preview.</param>
    /// <param name="connections">The backend, the probe and the templates.</param>
    /// <param name="progress">Told when the walk starts and as each node finishes.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<DeployResult> RunAsync(DeployInput input, Connections connections, Action<Progress>? progress, CancellationToken cancellationToken)
    {
        var loaded = await WorkloadInputs.LoadAsync(input.Workload, input.Environment, input.Catalog, cancellationToken);
        if (loaded.Value is not { } inputs)
        {
            return Failed(loaded.Errors);
        }

        if (!input.Preview && Checks.Gate(inputs.Environment) is { } gated)
        {
            return new DeployResult([], gated, Outcome.Failed, null);
        }

        var graph = inputs.Resolve();
        if (graph.Errors.Count > 0)
        {
            return Failed(graph.Errors);
        }

        var (orchestrator, misfits) = Walk.Prepare(input.Catalog, graph, inputs.Catalog, inputs.Environment, connections);
        if (orchestrator is null)
        {
            return Failed(misfits);
        }

        progress?.Invoke(new WorkloadStarted(graph.Workload));
        var finished = Walk.Reporting(progress);
        var run = input.Preview
            ? await orchestrator.PreviewAsync(graph, finished, cancellationToken)
            : await orchestrator.DeployAsync(graph, finished, cancellationToken);
        return new DeployResult([], null, run.Succeeded ? Outcome.Succeeded : Outcome.Failed, run);
    }

    private static DeployResult Failed(IReadOnlyList<LoadError> errors) => new(errors, null, Outcome.Failed, null);
}
