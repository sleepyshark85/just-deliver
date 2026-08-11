using jd.core.bp;

namespace jd.resolver;

/// <summary>
/// One deployment to execute, with the edges needed to schedule it. Parameters still
/// contain unresolved ${deployment.&lt;id&gt;.&lt;output&gt;} tokens - those values do not
/// exist until the referenced deployment has run, so the executor substitutes them.
/// </summary>
public class ResolvedDeployment
{
    /// <summary>Deployment id from the mapping file.</summary>
    public string Id { get; set; } = "";

    /// <summary>Resource type whose mapping produced this deployment.</summary>
    public string ResourceType { get; set; } = "";

    public DeploymentPackage Package { get; set; } = null!;

    /// <summary>Ids this deployment must run after.</summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary>
    /// Zero-based scheduling depth: everything at the same depth has no dependency on
    /// anything else at that depth and may run concurrently. Depth is a hint - starting
    /// each deployment as soon as its own DependsOn complete is strictly better than
    /// waiting for a whole depth to finish, since durations vary widely.
    /// </summary>
    public int Depth { get; set; }

    /// <summary>
    /// Set only on the deployment marked hosts_container. Values still carry
    /// ${deployment.*} tokens. Nothing consumes these yet - no runtime definition accepts
    /// app settings - but they are what determines this deployment's extra dependencies.
    /// </summary>
    public Dictionary<string, string>? ContainerVariables { get; set; }
}

public class ResolutionResult
{
    /// <summary>Topologically ordered; ties broken by id so the order is stable.</summary>
    public List<ResolvedDeployment> Deployments { get; set; } = new();

    /// <summary>Resource types resolved, including policy-attached and implicit ones.</summary>
    public List<string> ResourceTypes { get; set; } = new();
}

public class ResolutionException : Exception
{
    public ResolutionException(string message) : base(message)
    {
    }
}
