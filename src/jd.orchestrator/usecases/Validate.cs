using jd.resolver.workload;

namespace jd.orchestrator.usecases;

/// <summary><c>validate</c>: checks a workload definition against its schema and rules (docs/architecture/application.md section 3).</summary>
public static class Validate
{
    /// <summary>Checks <paramref name="input"/>'s workload; its errors name the document as the input does.</summary>
    /// <param name="input">The workload to check.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static async Task<ValidateResult> RunAsync(ValidateInput input, CancellationToken cancellationToken)
    {
        var loaded = await WorkloadFile.ParseAsync(input.Workload.Name, input.Workload.Text(), cancellationToken);
        return new ValidateResult(loaded.Errors, loaded.Errors.Count == 0 ? Outcome.Succeeded : Outcome.Failed);
    }
}
