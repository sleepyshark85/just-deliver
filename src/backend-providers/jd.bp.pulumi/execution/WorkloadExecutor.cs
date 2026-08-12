using jd.bp.pulumi.planning;
using jd.bp.pulumi.types;
using jd.core.bp;
using System.Text.RegularExpressions;

namespace jd.bp.pulumi.execution;

/// <summary>
/// Runs a plan. The only component that touches the backend, and the only one that can resolve
/// ${deployment.*} and ${identity.*} - those values do not exist until something has been
/// provisioned, which is why the resolver leaves them as tokens.
/// </summary>
public class WorkloadExecutor
{
    private static readonly Regex Deferred = new(@"\$\{(deployment|identity)\.([a-zA-Z0-9_.-]+)\}", RegexOptions.Compiled);
    private static readonly Regex DeploymentReference = new(@"\$\{deployment\.([a-zA-Z0-9_-]+)\.([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

    private readonly IBackEndProvider _backend;
    private readonly IDefinitionStore _definitions;
    private readonly BackendTypeCatalog _catalog;
    private readonly ExecutionOptions _options;

    public WorkloadExecutor(
        IBackEndProvider backend,
        IDefinitionStore definitions,
        BackendTypeCatalog catalog,
        ExecutionOptions? options = null)
    {
        _backend = backend;
        _definitions = definitions;
        _catalog = catalog;
        _options = options ?? new ExecutionOptions();
    }

    public async Task<WorkloadExecutionResult> ExecuteAsync(WorkloadPlan plan, CancellationToken cancellationToken = default)
    {
        var result = new WorkloadExecutionResult();
        var outputs = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var pending = plan.Deployments.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var running = new Dictionary<Task<DeploymentOutcome>, PlannedDeployment>();

        // A work queue rather than depth barriers: a deployment starts as soon as its own
        // dependencies finish. Durations range from seconds to minutes, so waiting for a whole
        // depth wastes most of it.
        while (pending.Count > 0 || running.Count > 0)
        {
            foreach (var deployment in Ready(pending, completed, failed).Take(_options.MaxConcurrency - running.Count).ToList())
            {
                pending.Remove(deployment.Key);
                running[RunAsync(deployment, plan, outputs, result, cancellationToken)] = deployment;
            }

            if (running.Count == 0)
            {
                // Nothing runnable and nothing running: everything left depends on a failure.
                result.Skipped.AddRange(pending.Keys.Order(StringComparer.Ordinal));
                break;
            }

            var finished = await Task.WhenAny(running.Keys);
            var source = running[finished];
            running.Remove(finished);

            var outcome = await finished;
            result.Deployments.Add(outcome);

            if (outcome.Error is not null)
            {
                failed.Add(outcome.Key);
                continue;
            }

            completed.Add(outcome.Key);
            outputs[outcome.Key] = outcome.Outputs;

            // A resource's abstract outputs become available once all of its deployments are in.
            PublishResourceOutputs(plan, source.ResourceName, completed, outputs, result);
        }

        return result;
    }

    private static IEnumerable<PlannedDeployment> Ready(
        Dictionary<string, PlannedDeployment> pending,
        HashSet<string> completed,
        HashSet<string> failed) =>
        pending.Values
            .Where(d => d.DependsOn.All(completed.Contains))
            .Where(d => !d.DependsOn.Any(failed.Contains))
            .OrderBy(d => d.Key, StringComparer.Ordinal);

    private async Task<DeploymentOutcome> RunAsync(
        PlannedDeployment deployment,
        WorkloadPlan plan,
        Dictionary<string, Dictionary<string, string?>> outputs,
        WorkloadExecutionResult result,
        CancellationToken cancellationToken)
    {
        var outcome = new DeploymentOutcome
        {
            Key = deployment.Key,
            StackName = deployment.StackName,
            Definition = deployment.Definition,
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parameters = Resolve(deployment, outputs, result.IdentityPrincipalId);
            ExpandRole(parameters, deployment.Key);

            var package = new DeploymentPackage
            {
                Name = deployment.StackName,
                Version = plan.Version,
                DeploymentContent = await _definitions.GetProgramAsync(deployment.Definition),
                DeploymentDefaultParametersContent = await _definitions.GetDefaultParametersAsync(deployment.Definition),
                DeploymentParameters = parameters.ToDictionary(p => p.Key, p => new ConfigEntry(p.Value)),
            };

            var deployed = _options.Preview
                ? await _backend.PreviewAsync(package)
                : await _backend.DeployAsync(package);

            outcome.Outputs = deployed.Outputs?.ToDictionary(o => o.Key, o => o.Value.Value) ?? new Dictionary<string, string?>();
            outcome.Summary = deployed.Summary ?? new Dictionary<string, int>();
            outcome.Changes = deployed.Changes ?? new List<ResourceChange>();

            // The workload identity is a property of the workload, not of the runtime that
            // happens to materialise it - captured here so grants never reach into another
            // resource type's outputs.
            if (result.IdentityPrincipalId is null && outcome.Outputs.TryGetValue("webAppPrincipalId", out var principal)
                && !string.IsNullOrEmpty(principal))
            {
                result.IdentityPrincipalId = principal;
            }
        }
        catch (Exception exception)
        {
            // Fail fast, no rollback: dependents are skipped rather than attempted against
            // half-provisioned state.
            outcome.Error = exception.Message;
        }

        return outcome;
    }

    /// <summary>
    /// Substitutes what the resolver deliberately left behind. ${deployment.x} inside a
    /// resource's deployment means that resource's own x; a foundation id is workload-scoped.
    /// </summary>
    private Dictionary<string, string> Resolve(
        PlannedDeployment deployment,
        Dictionary<string, Dictionary<string, string?>> outputs,
        string? identityPrincipalId)
    {
        var foundation = _catalog.Shared.Foundation.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        return deployment.Parameters.ToDictionary(
            p => p.Key,
            p => Deferred.Replace(p.Value, match =>
            {
                if (match.Groups[1].Value == "identity")
                {
                    return identityPrincipalId
                           ?? throw new ExecutionException(
                               $"'{deployment.Key}' needs the workload identity, but nothing has produced one yet.");
                }

                var reference = DeploymentReference.Match(match.Value);
                var (id, output) = (reference.Groups[1].Value, reference.Groups[2].Value);
                var key = foundation.Contains(id) || string.IsNullOrEmpty(deployment.ResourceName)
                    ? id
                    : $"{deployment.ResourceName}.{id}";

                if (!outputs.TryGetValue(key, out var available))
                {
                    throw new ExecutionException($"'{deployment.Key}' reads '{key}.{output}', which has not been deployed.");
                }

                return available.GetValueOrDefault(output)
                       ?? throw new ExecutionException($"'{deployment.Key}' reads '{key}.{output}', which the deployment did not output.");
            }));
    }

    /// <summary>
    /// Turns a role alias into the full role definition path, taking the subscription from the
    /// target scope so mapping files never carry subscription ids.
    /// </summary>
    private void ExpandRole(Dictionary<string, string> parameters, string deploymentKey)
    {
        if (!parameters.Remove("role", out var alias))
        {
            return;
        }

        if (!_catalog.Shared.Roles.TryGetValue(alias, out var roleId))
        {
            throw new ExecutionException($"'{deploymentKey}' uses role '{alias}', which is not in the shared roles table.");
        }

        if (!parameters.TryGetValue("scope", out var scope))
        {
            throw new ExecutionException($"'{deploymentKey}' uses a role but sets no scope to take the subscription from.");
        }

        var segments = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] != "subscriptions")
        {
            throw new ExecutionException($"'{deploymentKey}' has scope '{scope}', which is not an ARM resource id.");
        }

        parameters["roleDefinitionId"] =
            $"/subscriptions/{segments[1]}/providers/Microsoft.Authorization/roleDefinitions/{roleId}";
    }

    /// <summary>Evaluates a resource's translation outputs once all of its deployments are in.</summary>
    private void PublishResourceOutputs(
        WorkloadPlan plan,
        string resourceName,
        HashSet<string> completed,
        Dictionary<string, Dictionary<string, string?>> outputs,
        WorkloadExecutionResult result)
    {
        if (string.IsNullOrEmpty(resourceName))
        {
            return;
        }

        var deployments = plan.Deployments.Where(d => d.ResourceName == resourceName).ToList();
        if (!deployments.All(d => completed.Contains(d.Key)))
        {
            return;
        }

        var resource = plan.Resources.Single(r => r.Name == resourceName);
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (key, expression) in resource.Outputs)
        {
            resolved[key] = DeploymentReference.Replace(expression, match =>
            {
                var scoped = $"{resourceName}.{match.Groups[1].Value}";
                return outputs.TryGetValue(scoped, out var available)
                    ? available.GetValueOrDefault(match.Groups[2].Value) ?? ""
                    : "";
            });
        }

        result.ResourceOutputs[resourceName] = resolved;
    }
}

public class ExecutionOptions
{
    /// <summary>
    /// How many deployments may run at once. Safe above 1 because the foundation deployment
    /// always runs alone first, which installs the provider plugin before anything fans out -
    /// concurrent first runs would otherwise race on the shared PULUMI_HOME.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Plan and validate against the backend without creating anything.</summary>
    public bool Preview { get; set; }
}
