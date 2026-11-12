namespace jd.orchestrator.usecases;

/// <summary><c>preview</c>: resolves a workload for an environment over a catalog, creating nothing (docs/architecture/application.md section 3).</summary>
public static class Preview
{
    /// <summary>Resolves <paramref name="input"/>'s workload; the graph when it resolves, else the errors.</summary>
    /// <param name="input">The workload, the environment and the catalog.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<PreviewResult> RunAsync(PreviewInput input, CancellationToken cancellationToken)
    {
        var loaded = await WorkloadInputs.LoadAsync(input.Workload, input.Environment, input.Catalog, cancellationToken);
        if (loaded.Value is not { } inputs)
        {
            return new PreviewResult(loaded.Errors, Outcome.Failed, null);
        }

        var graph = inputs.Resolve();
        return graph.Errors.Count > 0 ? new PreviewResult(graph.Errors, Outcome.Failed, null) : new PreviewResult([], Outcome.Succeeded, graph);
    }
}
