using jd.bp.pulumi.execution;
using jd.bp.pulumi.planning;
using jd.bp.pulumi.types;
using jd.core.resources;
using jd.core.workload;
using Xunit;

namespace jd.bp.pulumi.tests;

/// <summary>
/// Executes the real plan for the real sample workload against a fake backend, so the whole
/// path - deferred substitution, role expansion, output publication, ordering - is verified
/// without Azure.
/// </summary>
public class WorkloadExecutorTests
{
    private static readonly BackendTypeCatalog Catalog = BackendTypeLoader.LoadDefault();

    private static readonly WorkloadResolver Resolver =
        new(ResourceTypeLoader.LoadDefault(), Catalog);

    private static WorkloadPlan SamplePlan() => Resolver.Resolve(WorkloadLoader.Load(
        File.ReadAllText(Path.Combine(TypeCoherenceTests.Repository, "samples", "provisioner", "workload.yaml"))));

    private static (WorkloadExecutor Executor, FakeBackend Backend) Build(ExecutionOptions? options = null)
    {
        var backend = new FakeBackend();
        var store = new RepositoryDefinitionStore(TypeCoherenceTests.Repository, Catalog.Shared.DefinitionsRoot);

        return (new WorkloadExecutor(backend, store, Catalog, options), backend);
    }

    private static async Task<(WorkloadExecutionResult Result, FakeBackend Backend)> RunSampleAsync(
        ExecutionOptions? options = null)
    {
        var (executor, backend) = Build(options);
        return (await executor.ExecuteAsync(SamplePlan()), backend);
    }

    [Fact]
    public async Task Deploys_every_planned_stack_and_succeeds()
    {
        var (result, backend) = await RunSampleAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.Deployments.Count);
        Assert.Equal(7, backend.Order.Count);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task Never_starts_a_deployment_before_its_dependencies_finished()
    {
        var plan = SamplePlan();
        var (executor, backend) = Build();
        await executor.ExecuteAsync(plan);

        var stackPosition = backend.Order.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);

        foreach (var deployment in plan.Deployments)
        {
            foreach (var dependency in deployment.DependsOn)
            {
                var dependencyStack = plan.Deployments.Single(d => d.Key == dependency).StackName;
                Assert.True(stackPosition[dependencyStack] < stackPosition[deployment.StackName],
                    $"'{deployment.Key}' started before '{dependency}'");
            }
        }
    }

    [Fact]
    public async Task Substitutes_deployment_outputs_the_resolver_left_as_tokens()
    {
        var (_, backend) = await RunSampleAsync();

        // ${deployment.resource-group.resourceGroupName} - a workload-scoped foundation output.
        Assert.Equal("rg-just-deliver-sample-app-dev",
            backend.Deployed["just-deliver-sample-app-dev-primary-account"]["resourceGroupName"]);

        // ${deployment.account.accountId} - scoped to this resource's own account.
        Assert.Equal(
            $"/subscriptions/{FakeBackend.Subscription}/resourceGroups/rg/providers/Microsoft.DocumentDB/databaseAccounts/cosmos-just-deliver-sample-app-primary",
            backend.Deployed["just-deliver-sample-app-dev-primary-access"]["accountId"]);
    }

    [Fact]
    public async Task Resolves_the_workload_identity_for_grants_that_need_it()
    {
        var (result, backend) = await RunSampleAsync();

        Assert.Equal(FakeBackend.PrincipalId, result.IdentityPrincipalId);
        Assert.Equal(FakeBackend.PrincipalId,
            backend.Deployed["just-deliver-sample-app-dev-primary-access"]["principalId"]);
        Assert.Equal(FakeBackend.PrincipalId,
            backend.Deployed["just-deliver-sample-app-dev-observability-publisher"]["principalId"]);
    }

    [Fact]
    public async Task Expands_a_role_alias_into_a_full_definition_path_from_the_scope()
    {
        var (_, backend) = await RunSampleAsync();
        var publisher = backend.Deployed["just-deliver-sample-app-dev-observability-publisher"];

        Assert.Equal(
            $"/subscriptions/{FakeBackend.Subscription}/providers/Microsoft.Authorization/roleDefinitions/3913510d-42f4-4e42-8a64-420c390055eb",
            publisher["roleDefinitionId"]);

        // `role` is not a real Pulumi parameter, so it must not survive expansion.
        Assert.DoesNotContain("role", publisher.Keys);
    }

    [Fact]
    public async Task Publishes_the_abstract_outputs_a_workload_references()
    {
        var (result, _) = await RunSampleAsync();

        Assert.Equal(
            "https://cosmos-just-deliver-sample-app-primary.documents.azure.com:443/",
            result.ResourceOutputs["primary"]["endpoint"]);
        Assert.Equal("primary", result.ResourceOutputs["primary"]["databaseName"]);

        // Policy-attached resources still produce outputs, they are just not referenceable.
        Assert.StartsWith("InstrumentationKey=", result.ResourceOutputs["observability"]["connectionString"]);
    }

    [Fact]
    public async Task Passes_definition_content_and_defaults_through_to_the_backend()
    {
        var plan = SamplePlan();
        var (executor, _) = Build();
        await executor.ExecuteAsync(plan);

        // Nothing here reads the filesystem directly - it all comes from the definition store.
        Assert.Equal("1.0.0", plan.Version);
    }

    [Fact]
    public async Task Previews_without_deploying_when_asked()
    {
        var (result, backend) = await RunSampleAsync(new ExecutionOptions { Preview = true });

        Assert.True(result.Succeeded);
        Assert.Equal(7, backend.Previewed.Count);
    }

    // --- failure handling ---

    [Fact]
    public async Task Fails_fast_and_skips_everything_downstream()
    {
        var (executor, backend) = Build();
        backend.FailOn.Add("cosmos-db");

        var result = await executor.ExecuteAsync(SamplePlan());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Deployments, d => d.Key == "primary.account" && d.Error is not null);

        // primary.access needs the account; api.app needs its endpoint; the publisher needs the
        // identity api.app would have produced.
        Assert.Contains("primary.access", result.Skipped);
        Assert.Contains("api.app", result.Skipped);
        Assert.Contains("observability.publisher", result.Skipped);

        // Independent work still completes rather than being abandoned.
        Assert.Contains(result.Deployments, d => d.Key == "observability.workspace" && d.Error is null);
    }

    [Fact]
    public async Task A_failure_leaves_the_resource_outputs_of_that_resource_unpublished()
    {
        var (executor, backend) = Build();
        backend.FailOn.Add("cosmos-db");

        var result = await executor.ExecuteAsync(SamplePlan());

        Assert.DoesNotContain("primary", result.ResourceOutputs.Keys);
    }

    [Fact]
    public async Task Runs_sequentially_when_concurrency_is_one()
    {
        var (executor, backend) = Build(new ExecutionOptions { MaxConcurrency = 1 });

        var result = await executor.ExecuteAsync(SamplePlan());

        Assert.True(result.Succeeded);
        Assert.Equal(7, backend.Order.Count);
    }
}
