using jd.resolver;
using jd.resolver.Models;
using Xunit;

namespace jd.resolver.tests;

public class WorkloadResolverTests : IClassFixture<RepositoryFixture>
{
    private readonly RepositoryFixture _repository;

    public WorkloadResolverTests(RepositoryFixture repository)
    {
        _repository = repository;
    }

    private Task<ResolutionResult> ResolveSampleAsync() =>
        _repository.CreateResolver().ResolveAsync(_repository.SampleWorkload());

    [Fact]
    public async Task Resolves_the_sample_workload_into_every_expected_deployment()
    {
        var result = await ResolveSampleAsync();

        Assert.Equal(
            new[] { "acr-pull", "app", "app-insights", "app-insights-publisher", "cosmos", "cosmos-access", "log-analytics", "resource-group" },
            result.Deployments.Select(d => d.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task Includes_policy_attached_and_implicit_types_the_workload_never_declared()
    {
        var result = await ResolveSampleAsync();

        // The sample only declares `database`.
        Assert.Equal(new[] { "database" }, _repository.SampleWorkload().Requires.Select(r => r.Type));
        Assert.Contains("foundation", result.ResourceTypes);
        Assert.Contains("monitoring", result.ResourceTypes);
        Assert.Contains("compute", result.ResourceTypes);
    }

    [Fact]
    public async Task Orders_dependencies_before_the_deployments_that_need_them()
    {
        var result = await ResolveSampleAsync();
        var position = result.Deployments.Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);

        foreach (var deployment in result.Deployments)
        {
            foreach (var dependency in deployment.DependsOn)
            {
                Assert.True(
                    position[dependency] < position[deployment.Id],
                    $"'{deployment.Id}' runs before its dependency '{dependency}'");
            }
        }
    }

    [Fact]
    public async Task Derives_the_container_variable_edge_that_no_mapping_file_states()
    {
        var result = await ResolveSampleAsync();
        var app = result.Deployments.Single(d => d.Id == "app");

        // COSMOS_ENDPOINT: ${resources.database.endpoint} -> database.yml outputs.endpoint
        // -> ${deployment.cosmos.documentEndpoint}. Nothing in compute.yml mentions cosmos.
        Assert.Contains("cosmos", app.DependsOn);
        Assert.Equal("${deployment.cosmos.documentEndpoint}", app.ContainerVariables!["COSMOS_ENDPOINT"]);
    }

    [Fact]
    public async Task Groups_deployments_into_depths_that_can_run_concurrently()
    {
        var result = await ResolveSampleAsync();
        var depths = result.Deployments.GroupBy(d => d.Depth)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Id).OrderBy(i => i).ToArray());

        Assert.Equal(new[] { "resource-group" }, depths[0]);
        Assert.Equal(new[] { "cosmos", "log-analytics" }, depths[1]);
        Assert.Equal(new[] { "app", "app-insights" }, depths[2]);
        Assert.Equal(new[] { "acr-pull", "app-insights-publisher", "cosmos-access" }, depths[3]);
    }

    [Fact]
    public async Task Substitutes_workload_and_environment_values_but_leaves_deployment_tokens()
    {
        var result = await ResolveSampleAsync();
        var cosmos = result.Deployments.Single(d => d.Id == "cosmos");

        Assert.Equal("cosmos-just-deliver-sample-app-dev", cosmos.Package.DeploymentParameters["accountName"].Value);
        Assert.Equal("southeastasia", cosmos.Package.DeploymentParameters["location"].Value);

        // Unresolvable until resource-group has run, so it survives for the executor.
        Assert.Equal(
            "${deployment.resource-group.resourceGroupName}",
            cosmos.Package.DeploymentParameters["resourceGroupName"].Value);
    }

    [Fact]
    public async Task Applies_the_environment_overlay_over_base_parameters()
    {
        var result = await ResolveSampleAsync();
        var app = result.Deployments.Single(d => d.Id == "app");

        // compute.yml sets the F1/Free ladder rung for dev only.
        Assert.Equal("F1", app.Package.DeploymentParameters["skuName"].Value);
        Assert.Equal("Free", app.Package.DeploymentParameters["skuTier"].Value);
    }

    [Fact]
    public async Task Keeps_yaml_booleans_lowercase_for_pulumi_config()
    {
        var result = await ResolveSampleAsync();
        var cosmos = result.Deployments.Single(d => d.Id == "cosmos");

        // Pulumi rejects .NET's "True"; the raw scalar text has to survive.
        Assert.Equal("true", cosmos.Package.DeploymentParameters["enableFreeTier"].Value);
    }

    [Fact]
    public async Task Names_stacks_per_workload_so_two_workloads_cannot_share_state()
    {
        var result = await ResolveSampleAsync();

        Assert.Equal("just-deliver-sample-app-dev-cosmos", result.Deployments.Single(d => d.Id == "cosmos").Package.Name);
        Assert.All(result.Deployments, d => Assert.StartsWith("just-deliver-sample-app-dev-", d.Package.Name));
    }

    [Fact]
    public async Task Takes_the_version_from_the_image_tag()
    {
        var result = await ResolveSampleAsync();

        Assert.All(result.Deployments, d => Assert.Equal("1.0.0", d.Package.Version));
    }

    [Fact]
    public async Task Loads_definition_content_and_defaults_from_the_store()
    {
        var result = await ResolveSampleAsync();
        var cosmos = result.Deployments.Single(d => d.Id == "cosmos");

        Assert.Contains("azure-native:cosmosdb:DatabaseAccount", cosmos.Package.DeploymentContent);
        Assert.Contains("cosmos-db:enableFreeTier", cosmos.Package.DeploymentDefaultParametersContent);

        // resource-group has no Pulumi.default.yaml.
        Assert.Null(result.Deployments.Single(d => d.Id == "resource-group").Package.DeploymentDefaultParametersContent);
    }

    // --- rejections ---

    private WorkloadDocument Workload(string requires = "  - type: database", string variables = "") =>
        MappingLoader.LoadWorkload($"""
            apiVersion: just-deliver/v1
            kind: Workload
            metadata:
              name: app
              team: team
              environment: dev
            container:
              image: registry.azurecr.io/app:2.1.0
              variables:
            {variables}
            requires:
            {requires}
            """);

    [Fact]
    public async Task Rejects_a_resource_type_with_no_mapping()
    {
        var error = await Assert.ThrowsAsync<ResolutionException>(() =>
            _repository.CreateResolver().ResolveAsync(Workload(requires: "  - type: cache")));

        Assert.Contains("No mapping for resource type 'cache'", error.Message);
    }

    [Fact]
    public async Task Rejects_a_variable_referencing_a_type_not_in_requires()
    {
        // monitoring is policy-attached, so it is provisioned but not referenceable.
        var error = await Assert.ThrowsAsync<ResolutionException>(() =>
            _repository.CreateResolver().ResolveAsync(
                Workload(variables: "    AI: ${resources.monitoring.connection_string}")));

        Assert.Contains("not listed in requires", error.Message);
    }

    [Fact]
    public async Task Rejects_a_variable_referencing_an_output_the_type_does_not_expose()
    {
        var error = await Assert.ThrowsAsync<ResolutionException>(() =>
            _repository.CreateResolver().ResolveAsync(
                Workload(variables: "    DB: ${resources.database.connection_string}")));

        Assert.Contains("exposes", error.Message);
        Assert.Contains("endpoint", error.Message);
    }

    [Fact]
    public async Task Rejects_an_unknown_environment()
    {
        var workload = _repository.SampleWorkload();
        workload.Metadata.Environment = "uat";

        var error = await Assert.ThrowsAsync<ResolutionException>(() =>
            _repository.CreateResolver().ResolveAsync(workload));

        Assert.Contains("No settings for environment 'uat'", error.Message);
    }

    [Fact]
    public async Task Rejects_an_override_outside_the_whitelist()
    {
        var workload = _repository.SampleWorkload();
        workload.Requires[0].Overrides = new Dictionary<string, string> { ["enableFreeTier"] = "false" };

        var error = await Assert.ThrowsAsync<ResolutionException>(() =>
            _repository.CreateResolver().ResolveAsync(workload));

        Assert.Contains("does not allow overriding 'enableFreeTier'", error.Message);
    }

    [Fact]
    public async Task Applies_a_whitelisted_override()
    {
        var workload = _repository.SampleWorkload();
        workload.Requires[0].Overrides = new Dictionary<string, string> { ["containerName"] = "events" };

        var result = await _repository.CreateResolver().ResolveAsync(workload);

        Assert.Equal("events", result.Deployments.Single(d => d.Id == "cosmos")
            .Package.DeploymentParameters["containerName"].Value);
    }
}
