using jd.bp.pulumi.types;
using jd.core.resources;
using jd.core.workload;
using System.Text.RegularExpressions;

namespace jd.bp.pulumi.planning;

/// <summary>
/// Turns a workload definition into an ordered deployment plan, using the core type contracts
/// and this backend's translations. Pure: no Azure, no Pulumi, no filesystem.
///
/// The algorithm is backend-neutral - expand, substitute, order - and only its inputs are
/// Pulumi-shaped. Worth hoisting once a second backend exists rather than duplicating it.
/// </summary>
public class WorkloadResolver
{
    private static readonly Regex ResourceReference =
        new(@"\$\{resource\.([a-zA-Z0-9_-]+)\.([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

    private static readonly Regex DeploymentReference =
        new(@"\$\{deployment\.([a-zA-Z0-9_-]+)\.([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

    private static readonly Regex Expression = new(@"\$\{([a-zA-Z0-9_.\-]+)\}", RegexOptions.Compiled);

    private readonly ResourceTypeCatalog _core;
    private readonly BackendTypeCatalog _backend;

    public WorkloadResolver(ResourceTypeCatalog core, BackendTypeCatalog backend)
    {
        _core = core;
        _backend = backend;
    }

    public WorkloadPlan Resolve(WorkloadDocument workload)
    {
        var environment = workload.Metadata.Environment;
        if (!_backend.Shared.Environments.TryGetValue(environment, out var environmentSettings))
        {
            throw new PlanningException(
                $"No settings for environment '{environment}'. Known: {Join(_backend.Shared.Environments.Keys)}.");
        }

        var resources = WithPolicyAttached(workload);
        ValidateResources(resources);
        ValidateReferences(workload, resources);

        var plan = new WorkloadPlan
        {
            Version = Version(workload),
            Resources = resources.Select(r => new PlannedResource
            {
                Name = r.Name,
                Type = r.Type,
                PolicyAttached = r.PolicyAttached,
                Outputs = _backend.Get(r.Type).Outputs.ToDictionary(o => o.Key, o => o.Value),
            }).ToList(),
        };

        // Foundation first - it is workload-scoped, so its keys are bare ids.
        foreach (var deployment in _backend.Shared.Foundation)
        {
            plan.Deployments.Add(Build(deployment, resource: null, workload, environmentSettings, environment));
        }

        foreach (var resource in resources)
        {
            foreach (var deployment in _backend.Get(resource.Type).Deployments)
            {
                plan.Deployments.Add(Build(deployment, resource, workload, environmentSettings, environment));
            }
        }

        AddReferenceEdges(plan, workload, resources);
        Order(plan.Deployments);

        return plan;
    }

    /// <summary>Declared resources plus the ones policy attaches regardless.</summary>
    private List<WorkloadResource> WithPolicyAttached(WorkloadDocument workload)
    {
        var resources = workload.Resources.ToList();

        foreach (var attached in _backend.Shared.PolicyAttached)
        {
            if (resources.Any(r => r.Name == attached.Name))
            {
                throw new PlanningException(
                    $"'{attached.Name}' is attached by policy, so a workload cannot declare a resource with that name.");
            }

            resources.Add(new WorkloadResource
            {
                Name = attached.Name,
                Type = attached.Type,
                PolicyAttached = true,
            });
        }

        return resources;
    }

    private void ValidateResources(List<WorkloadResource> resources)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            if (string.IsNullOrWhiteSpace(resource.Name))
            {
                throw new PlanningException($"A resource of type '{resource.Type}' has no name.");
            }

            if (!seen.Add(resource.Name))
            {
                throw new PlanningException($"Two resources are named '{resource.Name}'. Names must be unique within a workload.");
            }

            if (!_core.TryGet(resource.Type, out var contract))
            {
                throw new PlanningException(
                    $"'{resource.Name}' has unknown type '{resource.Type}'. Known: {Join(_core.Names.Order())}.");
            }

            if (!_backend.Supports(resource.Type))
            {
                throw new PlanningException(
                    $"'{resource.Name}' needs type '{resource.Type}', which this backend cannot provision. Supported: {Join(_backend.Types.Order())}.");
            }

            foreach (var (property, value) in resource.Properties)
            {
                if (!contract.Properties.TryGetValue(property, out var descriptor))
                {
                    throw new PlanningException(
                        $"'{resource.Name}' sets '{property}', which type '{resource.Type}' does not declare. It accepts: {Join(contract.Properties.Keys.Order())}.");
                }

                if (descriptor.Type == "enum" && value is string chosen && !descriptor.Values.Contains(chosen))
                {
                    throw new PlanningException(
                        $"'{resource.Name}.{property}' is '{chosen}', which is not one of {Join(descriptor.Values)}.");
                }
            }

            foreach (var required in contract.RequiredProperties.Where(p => !resource.Properties.ContainsKey(p)))
            {
                throw new PlanningException($"'{resource.Name}' is missing required property '{required}'.");
            }
        }
    }

    /// <summary>
    /// A workload may only reference resources it declared, and only outputs the type exposes.
    /// Policy-attached resources are provisioned but not referenceable - their outputs have to
    /// reach a container by injection instead.
    /// </summary>
    private void ValidateReferences(WorkloadDocument workload, List<WorkloadResource> resources)
    {
        var declared = workload.Resources.ToDictionary(r => r.Name, r => r.Type, StringComparer.Ordinal);

        foreach (var resource in workload.Resources)
        {
            foreach (var (property, value) in resource.Properties)
            {
                foreach (var text in Texts(value))
                {
                    foreach (var match in ResourceReference.Matches(text).Cast<Match>())
                    {
                        var (target, key) = (match.Groups[1].Value, match.Groups[2].Value);

                        if (!declared.TryGetValue(target, out var targetType))
                        {
                            throw new PlanningException(
                                $"'{resource.Name}.{property}' references '{target}', which the workload does not declare.");
                        }

                        if (!_core.Get(targetType).Outputs.ContainsKey(key))
                        {
                            throw new PlanningException(
                                $"'{resource.Name}.{property}' reads '{target}.{key}', but type '{targetType}' exposes {Join(_core.Get(targetType).Outputs.Keys.Order())}.");
                        }
                    }
                }
            }
        }
    }

    private PlannedDeployment Build(
        BackendDeployment deployment,
        WorkloadResource? resource,
        WorkloadDocument workload,
        Dictionary<string, string> environmentSettings,
        string environment)
    {
        var parameters = new Dictionary<string, string>(deployment.Parameters, StringComparer.Ordinal);

        // A parameter set contributes the branch matching the value this resource chose.
        foreach (var (property, branches) in deployment.ParameterSets)
        {
            var chosen = resource?.Scalar(property)
                         ?? (resource is null ? null : _core.Get(resource.Type).Properties[property].Default);

            if (chosen is null)
            {
                throw new PlanningException(
                    $"'{deployment.Id}' varies on '{property}' but no value or default was found.");
            }

            if (!branches.TryGetValue(chosen, out var branch))
            {
                throw new PlanningException(
                    $"'{deployment.Id}' has no branch for {property}='{chosen}'.");
            }

            foreach (var (parameter, value) in branch)
            {
                parameters[parameter] = value;
            }
        }

        var values = StaticValues(resource, workload, environmentSettings, environment);
        var substituted = parameters.ToDictionary(
            p => p.Key,
            p => Substitute(p.Value, values, deployment.Id));

        var key = resource is null ? deployment.Id : $"{resource.Name}.{deployment.Id}";

        return new PlannedDeployment
        {
            Key = key,
            ResourceName = resource?.Name ?? "",
            ResourceType = resource?.Type ?? "",
            DeploymentId = deployment.Id,
            Definition = deployment.Definition,
            StackName = $"{workload.Metadata.Name}-{environment}-{key.Replace('.', '-')}",
            Parameters = substituted,
            DependsOn = DependencyKeys(substituted.Values, resource).ToList(),
        };
    }

    private Dictionary<string, string> StaticValues(
        WorkloadResource? resource,
        WorkloadDocument workload,
        Dictionary<string, string> environmentSettings,
        string environment)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workload.name"] = workload.Metadata.Name,
            ["workload.team"] = workload.Metadata.Team,
            ["workload.environment"] = environment,
        };

        foreach (var (key, value) in environmentSettings)
        {
            values[$"env.{key}"] = value;
        }

        if (resource is not null)
        {
            values["resource.name"] = resource.Name;

            var contract = _core.Get(resource.Type);
            foreach (var (property, descriptor) in contract.Properties)
            {
                var value = resource.Scalar(property) ?? descriptor.Default;
                if (value is not null)
                {
                    values[$"property.{property}"] = value;
                }
            }
        }

        return values;
    }

    /// <summary>
    /// Resolves what is statically known. ${deployment.*} and ${identity.*} are left in place:
    /// those values do not exist until something has been provisioned.
    /// </summary>
    private static string Substitute(string template, Dictionary<string, string> values, string deploymentId) =>
        Expression.Replace(template, match =>
        {
            var path = match.Groups[1].Value;

            if (values.TryGetValue(path, out var value))
            {
                return value;
            }

            if (path.StartsWith("deployment.") || path.StartsWith("identity."))
            {
                return match.Value;
            }

            throw new PlanningException($"'{deploymentId}' references '${{{path}}}', which is not a known expression.");
        });

    /// <summary>
    /// Edges implied by deferred references. ${deployment.x} inside a resource's translation
    /// means that resource's own x; a foundation id is workload-scoped. ${identity.*} depends
    /// on whatever produces a principal id.
    /// </summary>
    private IEnumerable<string> DependencyKeys(IEnumerable<string> parameters, WorkloadResource? resource)
    {
        var foundation = _backend.Shared.Foundation.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var keys = new List<string>();

        foreach (var value in parameters)
        {
            foreach (var match in DeploymentReference.Matches(value).Cast<Match>())
            {
                var id = match.Groups[1].Value;
                keys.Add(foundation.Contains(id) || resource is null ? id : $"{resource.Name}.{id}");
            }
        }

        return keys.Distinct();
    }

    /// <summary>
    /// Two edges no file states: a workload variable referencing another resource's output, and
    /// ${identity.principalId} resolving to whichever deployment produces one.
    /// </summary>
    private void AddReferenceEdges(WorkloadPlan plan, WorkloadDocument workload, List<WorkloadResource> resources)
    {
        var byKey = plan.Deployments.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var identityProducers = IdentityProducers(plan).ToList();

        foreach (var deployment in plan.Deployments)
        {
            if (deployment.Parameters.Values.Any(v => v.Contains("${identity.")))
            {
                foreach (var producer in identityProducers.Where(p => p != deployment.Key))
                {
                    Add(deployment, producer);
                }
            }
        }

        // A resource whose property references another resource's output cannot be provisioned
        // before the deployments producing that output.
        foreach (var resource in workload.Resources)
        {
            foreach (var text in resource.Properties.Values.SelectMany(Texts))
            {
                foreach (var match in ResourceReference.Matches(text).Cast<Match>())
                {
                    var target = match.Groups[1].Value;
                    var outputKey = match.Groups[2].Value;
                    var targetResource = resources.First(r => r.Name == target);
                    var expression = _backend.Get(targetResource.Type).Outputs.GetValueOrDefault(outputKey);

                    if (expression is null)
                    {
                        continue;
                    }

                    foreach (var producerId in DeploymentReference.Matches(expression).Cast<Match>()
                                 .Select(m => m.Groups[1].Value))
                    {
                        foreach (var consumer in plan.Deployments.Where(d => d.ResourceName == resource.Name))
                        {
                            Add(consumer, $"{target}.{producerId}");
                        }
                    }
                }
            }
        }

        void Add(PlannedDeployment deployment, string dependency)
        {
            if (byKey.ContainsKey(dependency) && dependency != deployment.Key && !deployment.DependsOn.Contains(dependency))
            {
                deployment.DependsOn.Add(dependency);
            }
        }
    }

    /// <summary>Deployments whose translation exposes a principalId - the identity's origin.</summary>
    private IEnumerable<string> IdentityProducers(WorkloadPlan plan)
    {
        foreach (var resource in plan.Resources)
        {
            var outputs = _backend.Get(resource.Type).Outputs;
            if (!outputs.TryGetValue("principalId", out var expression))
            {
                continue;
            }

            foreach (var id in DeploymentReference.Matches(expression).Cast<Match>().Select(m => m.Groups[1].Value))
            {
                yield return $"{resource.Name}.{id}";
            }
        }
    }

    /// <summary>Kahn's algorithm, ties broken by key so a given input always plans identically.</summary>
    private static void Order(List<PlannedDeployment> deployments)
    {
        var byKey = deployments.ToDictionary(d => d.Key, StringComparer.Ordinal);

        foreach (var deployment in deployments)
        {
            var unknown = deployment.DependsOn.FirstOrDefault(d => !byKey.ContainsKey(d));
            if (unknown is not null)
            {
                throw new PlanningException($"'{deployment.Key}' depends on '{unknown}', which is not in the plan.");
            }
        }

        var remaining = deployments.ToDictionary(d => d.Key, d => d.DependsOn.ToHashSet(StringComparer.Ordinal));
        var ordered = new List<PlannedDeployment>();
        var depth = 0;

        while (remaining.Count > 0)
        {
            var ready = remaining.Where(r => r.Value.Count == 0).Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (ready.Count == 0)
            {
                throw new PlanningException($"Dependency cycle between: {Join(remaining.Keys.Order())}.");
            }

            foreach (var key in ready)
            {
                byKey[key].Depth = depth;
                ordered.Add(byKey[key]);
                remaining.Remove(key);
            }

            foreach (var dependencies in remaining.Values)
            {
                dependencies.ExceptWith(ready);
            }

            depth++;
        }

        deployments.Clear();
        deployments.AddRange(ordered);
    }

    /// <summary>Every string inside a property value, so map-valued properties are searched too.</summary>
    private static IEnumerable<string> Texts(object? value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string text:
                yield return text;
                break;
            case System.Collections.IDictionary map:
                foreach (var entry in map.Values)
                {
                    foreach (var text in Texts(entry))
                    {
                        yield return text;
                    }
                }

                break;
            case System.Collections.IEnumerable list:
                foreach (var entry in list)
                {
                    foreach (var text in Texts(entry))
                    {
                        yield return text;
                    }
                }

                break;
        }
    }

    /// <summary>The tag of whichever runtime image the workload declares.</summary>
    private static string Version(WorkloadDocument workload)
    {
        var image = workload.Resources.Select(r => r.Scalar("image")).FirstOrDefault(i => !string.IsNullOrEmpty(i));
        var separator = image?.LastIndexOf(':') ?? -1;

        return separator > 0 && separator < image!.Length - 1 ? image[(separator + 1)..] : "latest";
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);
}
