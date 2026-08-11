using jd.core.bp;
using jd.resolver.Models;
using System.Text.RegularExpressions;

namespace jd.resolver;

/// <summary>
/// Turns a validated workload definition plus the platform mappings into an ordered set
/// of deployments. Pure apart from reading definition content: no Azure calls, no Pulumi.
/// </summary>
public class WorkloadResolver
{
    /// <summary>
    /// Implied by the presence of `container` rather than declared in `requires`, so the
    /// runtime resolves through the same machinery as everything else.
    /// </summary>
    private const string ImplicitComputeType = "compute";

    private static readonly Regex ResourceReference =
        new(@"\$\{resources\.([a-zA-Z0-9_-]+)\.([a-zA-Z0-9_-]+)\}", RegexOptions.Compiled);

    private readonly SharedSettings _shared;
    private readonly Dictionary<string, MappingDocument> _mappings;
    private readonly IDefinitionStore _definitions;

    public WorkloadResolver(SharedSettings shared, Dictionary<string, MappingDocument> mappings, IDefinitionStore definitions)
    {
        _shared = shared;
        _mappings = mappings;
        _definitions = definitions;
    }

    public async Task<ResolutionResult> ResolveAsync(WorkloadDocument workload)
    {
        var environment = workload.Metadata.Environment;
        if (!_shared.Environments.TryGetValue(environment, out var environmentSettings))
        {
            throw new ResolutionException(
                $"No settings for environment '{environment}'. Known: {Join(_shared.Environments.Keys)}.");
        }

        var declared = workload.Requires.Select(r => r.Type).ToHashSet();
        var resourceTypes = ResolveResourceTypes(declared);

        ValidateResourceReferences(workload, declared);

        var workloadValues = WorkloadValues(workload);
        var environmentValues = EnvironmentValues(environment, environmentSettings);
        var overrides = OverridesByType(workload);

        var resolved = new List<ResolvedDeployment>();
        foreach (var type in resourceTypes)
        {
            foreach (var deployment in _mappings[type].Deployments)
            {
                resolved.Add(await BuildAsync(
                    deployment, type, workload, environment, workloadValues, environmentValues, overrides));
            }
        }

        AttachContainerEdges(resolved, workload, declared);
        Order(resolved);

        return new ResolutionResult
        {
            Deployments = resolved,
            ResourceTypes = resourceTypes,
        };
    }

    /// <summary>requires ∪ policy_attached ∪ implicit compute, deduplicated and stable.</summary>
    private List<string> ResolveResourceTypes(HashSet<string> declared)
    {
        var types = new List<string>();

        void Add(string type)
        {
            if (types.Contains(type))
            {
                return;
            }

            if (!_mappings.ContainsKey(type))
            {
                throw new ResolutionException(
                    $"No mapping for resource type '{type}'. Mapped types: {Join(_mappings.Keys)}.");
            }

            types.Add(type);
        }

        foreach (var type in _shared.PolicyAttached)
        {
            Add(type);
        }

        Add(ImplicitComputeType);

        foreach (var type in declared.OrderBy(t => t))
        {
            Add(type);
        }

        return types;
    }

    /// <summary>
    /// A workload may only reference types it declared in `requires`. Policy-attached and
    /// implicit types are provisioned but not referenceable - their outputs have to reach a
    /// container by injection instead.
    /// </summary>
    private void ValidateResourceReferences(WorkloadDocument workload, HashSet<string> declared)
    {
        foreach (var (variable, value) in workload.Container.Variables)
        {
            foreach (var match in ResourceReference.Matches(value ?? "").Cast<Match>())
            {
                var (type, key) = (match.Groups[1].Value, match.Groups[2].Value);

                if (!declared.Contains(type))
                {
                    throw new ResolutionException(
                        $"Variable '{variable}' references resource type '{type}', which is not listed in requires ({Join(declared)}).");
                }

                if (!_mappings[type].Outputs.ContainsKey(key))
                {
                    throw new ResolutionException(
                        $"Variable '{variable}' references '{type}.{key}', but '{type}' exposes {Join(_mappings[type].Outputs.Keys)}.");
                }
            }
        }
    }

    private async Task<ResolvedDeployment> BuildAsync(
        MappingDeployment deployment,
        string resourceType,
        WorkloadDocument workload,
        string environment,
        Dictionary<string, string> workloadValues,
        Dictionary<string, string> environmentValues,
        Dictionary<string, Dictionary<string, string>> overrides)
    {
        // parameters, then the environment overlay, then team overrides.
        var parameters = new Dictionary<string, string>(deployment.Parameters);

        if (deployment.Environments.TryGetValue(environment, out var overlay))
        {
            foreach (var (key, value) in overlay)
            {
                parameters[key] = value;
            }
        }

        if (overrides.TryGetValue(resourceType, out var requested))
        {
            var whitelist = _mappings[resourceType].OverrideWhitelist;
            foreach (var (key, value) in requested)
            {
                if (!whitelist.Contains(key))
                {
                    throw new ResolutionException(
                        $"'{resourceType}' does not allow overriding '{key}'. Overrideable: {Join(whitelist)}.");
                }

                // Only applies to the deployment that actually declares the parameter.
                if (parameters.ContainsKey(key))
                {
                    parameters[key] = value;
                }
            }
        }

        var substituted = new Dictionary<string, ConfigEntry>();
        foreach (var (key, raw) in parameters)
        {
            var value = ExpressionTemplate.Substitute(raw, path =>
                workloadValues.TryGetValue(path, out var w) ? w
                : environmentValues.TryGetValue(path, out var e) ? e
                : null);

            foreach (var remaining in ExpressionTemplate.Paths(value))
            {
                if (!remaining.StartsWith("deployment."))
                {
                    throw new ResolutionException(
                        $"Parameter '{key}' on deployment '{deployment.Id}' references '${{{remaining}}}', which is not a known expression.");
                }
            }

            substituted[key] = new ConfigEntry(value);
        }

        return new ResolvedDeployment
        {
            Id = deployment.Id,
            ResourceType = resourceType,
            DependsOn = DependencyIds(substituted.Values.Select(v => v.Value)).ToList(),
            Package = new DeploymentPackage
            {
                Name = StackName(workload, deployment.Id),
                Version = Version(workload),
                DeploymentContent = await _definitions.GetProgramAsync(deployment.Definition),
                DeploymentDefaultParametersContent = await _definitions.GetDefaultParametersAsync(deployment.Definition),
                DeploymentParameters = substituted,
            },
            ContainerVariables = ResolveContainerVariables(deployment, workload),
        };
    }

    /// <summary>
    /// Resolves container.variables for the container-hosting deployment, turning
    /// ${resources.&lt;type&gt;.&lt;key&gt;} into the ${deployment.*} token behind it.
    /// Not yet consumable: no runtime definition accepts app settings.
    /// </summary>
    private Dictionary<string, string>? ResolveContainerVariables(MappingDeployment deployment, WorkloadDocument workload)
    {
        if (!deployment.HostsContainer)
        {
            return null;
        }

        return workload.Container.Variables.ToDictionary(
            variable => variable.Key,
            variable => ResourceReference.Replace(variable.Value ?? "", match =>
                _mappings[match.Groups[1].Value].Outputs[match.Groups[2].Value]));
    }

    private void AttachContainerEdges(List<ResolvedDeployment> resolved, WorkloadDocument workload, HashSet<string> declared)
    {
        var hosts = resolved.Where(r => r.ContainerVariables is not null).ToList();
        if (hosts.Count > 1)
        {
            throw new ResolutionException(
                $"More than one deployment sets hosts_container: {Join(hosts.Select(h => h.Id))}.");
        }

        var host = hosts.SingleOrDefault();
        if (host is null)
        {
            return;
        }

        foreach (var id in DependencyIds(host.ContainerVariables!.Values))
        {
            if (!host.DependsOn.Contains(id))
            {
                host.DependsOn.Add(id);
            }
        }
    }

    private static IEnumerable<string> DependencyIds(IEnumerable<string?> values) =>
        values
            .Where(v => v is not null)
            .SelectMany(v => ExpressionTemplate.Paths(v!))
            .Where(p => p.StartsWith("deployment."))
            .Select(p => p.Split('.')[1])
            .Distinct();

    /// <summary>
    /// Kahn's algorithm. Ties broken by id so a given input always yields the same order -
    /// an unstable sort would make previews and diffs differ run to run for no reason.
    /// </summary>
    private static void Order(List<ResolvedDeployment> resolved)
    {
        var byId = resolved.ToDictionary(r => r.Id);

        foreach (var deployment in resolved)
        {
            var unknown = deployment.DependsOn.FirstOrDefault(d => !byId.ContainsKey(d));
            if (unknown is not null)
            {
                throw new ResolutionException(
                    $"Deployment '{deployment.Id}' depends on '{unknown}', which is not part of this resolution.");
            }
        }

        var remaining = resolved.ToDictionary(r => r.Id, r => r.DependsOn.ToHashSet());
        var ordered = new List<ResolvedDeployment>();
        var depth = 0;

        while (remaining.Count > 0)
        {
            var ready = remaining.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).OrderBy(id => id).ToList();
            if (ready.Count == 0)
            {
                throw new ResolutionException(
                    $"Dependency cycle between: {Join(remaining.Keys.OrderBy(k => k))}.");
            }

            foreach (var id in ready)
            {
                byId[id].Depth = depth;
                ordered.Add(byId[id]);
                remaining.Remove(id);
            }

            foreach (var dependencies in remaining.Values)
            {
                dependencies.ExceptWith(ready);
            }

            depth++;
        }

        resolved.Clear();
        resolved.AddRange(ordered);
    }

    /// <summary>
    /// Mapping deployment ids are unique per mappings folder, not per workload, so the
    /// stack name has to carry the workload and environment too. This name becomes the
    /// Pulumi stack, the working directory, and the stack config filename - changing the
    /// scheme later orphans state.
    /// </summary>
    private static string StackName(WorkloadDocument workload, string deploymentId) =>
        $"{workload.Metadata.Name}-{workload.Metadata.Environment}-{deploymentId}";

    /// <summary>
    /// Taken from the container image tag. Provisional - see MVP_01_OPEN_ISSUES.md §2;
    /// this also decides where state lives, since it is part of the working directory.
    /// </summary>
    private static string Version(WorkloadDocument workload)
    {
        var separator = workload.Container.Image.LastIndexOf(':');
        return separator > 0 && separator < workload.Container.Image.Length - 1
            ? workload.Container.Image[(separator + 1)..]
            : "latest";
    }

    private static Dictionary<string, string> WorkloadValues(WorkloadDocument workload) => new()
    {
        ["workload.metadata.name"] = workload.Metadata.Name,
        ["workload.metadata.team"] = workload.Metadata.Team,
        ["workload.metadata.environment"] = workload.Metadata.Environment,
        ["workload.container.image"] = workload.Container.Image,
    };

    private static Dictionary<string, string> EnvironmentValues(string environment, Dictionary<string, string> settings)
    {
        // `name` is not a key in the environments table - the environment's own name is
        // injected, because every mapping uses ${env.name} for resource naming.
        var values = new Dictionary<string, string> { ["env.name"] = environment };
        foreach (var (key, value) in settings)
        {
            values[$"env.{key}"] = value;
        }

        return values;
    }

    private static Dictionary<string, Dictionary<string, string>> OverridesByType(WorkloadDocument workload) =>
        workload.Requires
            .Where(r => r.Overrides.Count > 0)
            .ToDictionary(r => r.Type, r => r.Overrides);

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);
}
