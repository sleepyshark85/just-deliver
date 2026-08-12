using jd.bp.pulumi.planning;
using jd.bp.pulumi.types;
using jd.core.resources;
using jd.core.workload;
using Xunit;

namespace jd.bp.pulumi.tests;

/// <summary>
/// Planned against the repository's real type contracts, translations and sample workload, so
/// these fail when any of the three drifts from the others.
/// </summary>
public class WorkloadResolverTests
{
    private static readonly WorkloadResolver Resolver =
        new(ResourceTypeLoader.LoadDefault(), BackendTypeLoader.LoadDefault());

    private static WorkloadPlan PlanSample() => Resolver.Resolve(SampleWorkload());

    private static WorkloadDocument SampleWorkload() =>
        WorkloadLoader.Load(File.ReadAllText(Path.Combine(
            TypeCoherenceTests.Repository, "samples", "provisioner", "workload.yaml")));

    private static WorkloadDocument Workload(string resources) => WorkloadLoader.Load($"""
        apiVersion: just-deliver/v1
        kind: Workload
        metadata:
          name: app
          team: team
          environment: dev
        resources:
        {resources}
        """);

    [Fact]
    public void Expands_two_declared_resources_into_every_deployment()
    {
        var plan = PlanSample();

        Assert.Equal(
            new[]
            {
                "api.app",
                "observability.component", "observability.publisher", "observability.workspace",
                "primary.access", "primary.account",
                "resource-group",
            },
            plan.Deployments.Select(d => d.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Attaches_observability_the_workload_never_declared()
    {
        var plan = PlanSample();

        Assert.Equal(new[] { "api", "primary" }, SampleWorkload().Resources.Select(r => r.Name));
        Assert.Contains(plan.Resources, r => r.Name == "observability" && r.PolicyAttached);
    }

    [Fact]
    public void Orders_dependencies_before_the_deployments_that_need_them()
    {
        var plan = PlanSample();
        var position = plan.Deployments.Select((d, i) => (d.Key, i)).ToDictionary(x => x.Key, x => x.i);

        foreach (var deployment in plan.Deployments)
        {
            foreach (var dependency in deployment.DependsOn)
            {
                Assert.True(position[dependency] < position[deployment.Key],
                    $"'{deployment.Key}' runs before its dependency '{dependency}'");
            }
        }
    }

    [Fact]
    public void Derives_the_two_edges_that_no_file_states()
    {
        var plan = PlanSample();

        // COSMOS_ENDPOINT: ${resource.primary.endpoint} -> database outputs.endpoint ->
        // ${deployment.account.documentEndpoint}. Nothing in computing.yml mentions a database.
        Assert.Contains("primary.account", plan.Deployments.Single(d => d.Key == "api.app").DependsOn);

        // ${identity.principalId} on the Cosmos grant resolves to whatever produces one.
        Assert.Contains("api.app", plan.Deployments.Single(d => d.Key == "primary.access").DependsOn);
    }

    [Fact]
    public void Groups_deployments_into_depths_that_can_run_concurrently()
    {
        var depths = PlanSample().Deployments
            .GroupBy(d => d.Depth)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        Assert.Equal(new[] { "resource-group" }, depths[0]);
        Assert.Equal(new[] { "observability.workspace", "primary.account" }, depths[1]);
        Assert.Equal(new[] { "api.app", "observability.component" }, depths[2]);
        Assert.Equal(new[] { "observability.publisher", "primary.access" }, depths[3]);
    }

    [Fact]
    public void Substitutes_what_is_known_and_defers_what_is_not()
    {
        var account = PlanSample().Deployments.Single(d => d.Key == "primary.account");

        Assert.Equal("cosmos-just-deliver-sample-app-primary", account.Parameters["accountName"]);
        Assert.Equal("primary", account.Parameters["databaseName"]);
        Assert.Equal("southeastasia", account.Parameters["location"]);

        // Unresolvable until resource-group has run, so it survives for the executor.
        Assert.Equal("${deployment.resource-group.resourceGroupName}", account.Parameters["resourceGroupName"]);
        Assert.Equal("${identity.principalId}",
            PlanSample().Deployments.Single(d => d.Key == "primary.access").Parameters["principalId"]);
    }

    [Fact]
    public void Applies_the_parameter_set_branch_for_the_class_chosen()
    {
        var plan = PlanSample();

        Assert.Equal("true", plan.Deployments.Single(d => d.Key == "primary.account").Parameters["enableFreeTier"]);
        Assert.Equal("F1", plan.Deployments.Single(d => d.Key == "api.app").Parameters["skuName"]);
    }

    [Fact]
    public void Falls_back_to_the_types_default_class_when_unset()
    {
        var plan = Resolver.Resolve(Workload("""
              - name: db
                type: database
            """));

        // observability declares no class either, and defaults to standard.
        Assert.Equal("true", plan.Deployments.Single(d => d.Key == "db.account").Parameters["enableFreeTier"]);
        Assert.Equal("31", plan.Deployments.Single(d => d.Key == "observability.workspace").Parameters["retentionInDays"]);
    }

    [Fact]
    public void Keeps_yaml_scalars_as_written_so_pulumi_config_stays_valid()
    {
        // .NET's bool.ToString() gives "True", which a `type: Boolean` program rejects.
        Assert.Equal("false", PlanSample().Deployments
            .Single(d => d.Key == "observability.component").Parameters["disableLocalAuth"]);
    }

    [Fact]
    public void Scopes_deployments_per_resource_so_two_of_a_type_cannot_collide()
    {
        var plan = Resolver.Resolve(Workload("""
              - name: primary
                type: database
              - name: analytics
                type: database
            """));

        Assert.Contains(plan.Deployments, d => d.Key == "primary.account");
        Assert.Contains(plan.Deployments, d => d.Key == "analytics.account");
        Assert.Equal("cosmos-app-primary", plan.Deployments.Single(d => d.Key == "primary.account").Parameters["accountName"]);
        Assert.Equal("cosmos-app-analytics", plan.Deployments.Single(d => d.Key == "analytics.account").Parameters["accountName"]);

        // A grant must reach its own account, not the other one.
        Assert.Contains("primary.account", plan.Deployments.Single(d => d.Key == "primary.access").DependsOn);
        Assert.DoesNotContain("analytics.account", plan.Deployments.Single(d => d.Key == "primary.access").DependsOn);
    }

    [Fact]
    public void Stack_names_carry_workload_and_environment_so_workloads_cannot_share_state()
    {
        var plan = PlanSample();

        Assert.Equal("just-deliver-sample-app-dev-primary-account",
            plan.Deployments.Single(d => d.Key == "primary.account").StackName);
        Assert.All(plan.Deployments, d => Assert.StartsWith("just-deliver-sample-app-dev-", d.StackName));
    }

    [Fact]
    public void Carries_the_output_expressions_the_executor_evaluates_afterwards()
    {
        var primary = PlanSample().Resources.Single(r => r.Name == "primary");

        Assert.Equal("${deployment.account.documentEndpoint}", primary.Outputs["endpoint"]);
    }

    // --- rejections ---

    [Fact]
    public void Rejects_an_unknown_resource_type()
    {
        var error = Assert.Throws<PlanningException>(() => Resolver.Resolve(Workload("""
              - name: q
                type: queue
            """)));

        Assert.Contains("unknown type 'queue'", error.Message);
    }

    [Fact]
    public void Rejects_a_property_the_type_does_not_declare()
    {
        var error = Assert.Throws<PlanningException>(() => Resolver.Resolve(Workload("""
              - name: db
                type: database
                sku: Premium_P2
            """)));

        Assert.Contains("sets 'sku'", error.Message);
    }

    [Fact]
    public void Rejects_a_class_value_outside_the_contract()
    {
        var error = Assert.Throws<PlanningException>(() => Resolver.Resolve(Workload("""
              - name: db
                type: database
                class: enormous
            """)));

        Assert.Contains("is 'enormous'", error.Message);
    }

    [Fact]
    public void Rejects_a_missing_required_property()
    {
        var error = Assert.Throws<PlanningException>(() => Resolver.Resolve(Workload("""
              - name: api
                type: computing
            """)));

        Assert.Contains("missing required property 'image'", error.Message);
    }

    [Fact]
    public void Rejects_duplicate_resource_names()
    {
        var error = Assert.Throws<PlanningException>(() => Resolver.Resolve(Workload("""
              - name: db
                type: database
              - name: db
                type: database
            """)));

        Assert.Contains("Two resources are named 'db'", error.Message);
    }

    [Fact]
    public void Rejects_a_reference_to_a_resource_the_workload_does_not_declare()
    {
        var error = Assert.Throws<PlanningException>(() => Resolver.Resolve(Workload("""
              - name: api
                type: computing
                image: registry/app:1
                variables:
                  DB: ${resource.nowhere.endpoint}
            """)));

        Assert.Contains("references 'nowhere'", error.Message);
    }

    [Fact]
    public void Rejects_a_reference_to_an_output_the_type_does_not_expose()
    {
        var error = Assert.Throws<PlanningException>(() => Resolver.Resolve(Workload("""
              - name: api
                type: computing
                image: registry/app:1
                variables:
                  DB: ${resource.db.host}
              - name: db
                type: database
            """)));

        Assert.Contains("exposes", error.Message);
        Assert.Contains("endpoint", error.Message);
    }

    [Fact]
    public void Rejects_referencing_a_policy_attached_resource()
    {
        // observability is provisioned but not referenceable - its outputs must be injected.
        var error = Assert.Throws<PlanningException>(() => Resolver.Resolve(Workload("""
              - name: api
                type: computing
                image: registry/app:1
                variables:
                  AI: ${resource.observability.connectionString}
            """)));

        Assert.Contains("which the workload does not declare", error.Message);
    }

    [Fact]
    public void Rejects_a_workload_shadowing_a_policy_attached_name()
    {
        var error = Assert.Throws<PlanningException>(() => Resolver.Resolve(Workload("""
              - name: observability
                type: database
            """)));

        Assert.Contains("attached by policy", error.Message);
    }

    [Fact]
    public void Rejects_an_unknown_environment()
    {
        var workload = SampleWorkload();
        workload.Metadata.Environment = "uat";

        Assert.Contains("No settings for environment 'uat'",
            Assert.Throws<PlanningException>(() => Resolver.Resolve(workload)).Message);
    }
}
