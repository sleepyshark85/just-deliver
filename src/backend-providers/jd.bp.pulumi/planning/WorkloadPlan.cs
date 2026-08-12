namespace jd.bp.pulumi.planning;

/// <summary>
/// What to deploy, in what order, for one workload. Parameters still carry
/// ${deployment.*} and ${identity.*} tokens: those values do not exist until the
/// deployments producing them have run, so the executor is the only thing that resolves them.
/// </summary>
public class WorkloadPlan
{
    /// <summary>Topologically ordered; ties broken by key so the same input always plans the same way.</summary>
    public List<PlannedDeployment> Deployments { get; set; } = new();

    /// <summary>
    /// Taken from the runtime image tag. Part of the backend's working directory, so it decides
    /// where state lives - provisional, see OPEN_ISSUES section 1.
    /// </summary>
    public string Version { get; set; } = "latest";

    /// <summary>Resources resolved, including policy-attached ones the workload never declared.</summary>
    public List<PlannedResource> Resources { get; set; } = new();
}

public class PlannedResource
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "";

    /// <summary>True when the platform added this rather than the team declaring it.</summary>
    public bool PolicyAttached { get; set; }

    /// <summary>
    /// Abstract output key -> expression, from the translation. Evaluated by the executor once
    /// this resource's deployments have completed, giving the values a workload's
    /// ${resource.&lt;name&gt;.&lt;key&gt;} references resolve to.
    /// </summary>
    public Dictionary<string, string> Outputs { get; set; } = new();
}

public class PlannedDeployment
{
    /// <summary>
    /// Unique within the plan. Scoped per resource instance - translation ids are unique per
    /// translation, not per workload, so two databases would both produce `account`.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>Empty for foundation deployments, which are workload-scoped.</summary>
    public string ResourceName { get; set; } = "";

    public string ResourceType { get; set; } = "";

    /// <summary>The id as written in the translation.</summary>
    public string DeploymentId { get; set; } = "";

    /// <summary>Folder name of the Pulumi definition to run.</summary>
    public string Definition { get; set; } = "";

    /// <summary>The Pulumi stack name. Carries workload and environment so two workloads never share state.</summary>
    public string StackName { get; set; } = "";

    /// <summary>Fully substituted apart from ${deployment.*} and ${identity.*}.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>Keys of deployments this must run after.</summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary>
    /// Scheduling depth: everything at one depth is mutually independent. A hint only -
    /// starting each deployment as its own dependencies complete beats waiting for a whole
    /// depth, since durations vary from seconds to minutes.
    /// </summary>
    public int Depth { get; set; }
}

public class PlanningException : Exception
{
    public PlanningException(string message) : base(message)
    {
    }
}
