using jd.core.bp;

namespace jd.bp.pulumi.execution;

public class WorkloadExecutionResult
{
    /// <summary>In completion order, so a partial failure shows what actually ran.</summary>
    public List<DeploymentOutcome> Deployments { get; set; } = new();

    /// <summary>
    /// Resource name -> abstract output key -> value, evaluated from each translation's
    /// `outputs:` block. This is what a workload's ${resource.&lt;name&gt;.&lt;key&gt;} references
    /// resolve to.
    /// </summary>
    public Dictionary<string, Dictionary<string, string?>> ResourceOutputs { get; set; } = new();

    /// <summary>Null until a deployment producing a principal id has completed.</summary>
    public string? IdentityPrincipalId { get; set; }

    /// <summary>Deployments that never ran because something they depended on failed.</summary>
    public List<string> Skipped { get; set; } = new();

    public bool Succeeded => Deployments.All(d => d.Error is null) && Skipped.Count == 0;
}

public class DeploymentOutcome
{
    public string Key { get; set; } = "";

    public string StackName { get; set; } = "";

    public string Definition { get; set; } = "";

    /// <summary>Raw Pulumi stack outputs, keyed as the definition declares them.</summary>
    public Dictionary<string, string?> Outputs { get; set; } = new();

    public Dictionary<string, int> Summary { get; set; } = new();

    public List<ResourceChange> Changes { get; set; } = new();

    /// <summary>Set when this deployment failed. Fail fast: dependents are skipped, not attempted.</summary>
    public string? Error { get; set; }
}

public class ExecutionException : Exception
{
    public ExecutionException(string message) : base(message)
    {
    }
}
