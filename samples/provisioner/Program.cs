using jd.bp.pulumi;
using jd.bp.pulumi.execution;
using jd.bp.pulumi.planning;
using jd.bp.pulumi.types;
using jd.core.bp;
using jd.core.resources;
using jd.core.workload;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// --plan     resolve and print the plan, touching nothing. Runs without Azure or Pulumi.
// --preview  run every stack through Pulumi preview instead of up.
// (default)  provision.
var planOnly = args.Contains("--plan");
var preview = args.Contains("--preview");

var repositoryRoot = FindRepositoryRoot();
var workloadPath = args.FirstOrDefault(a => a.EndsWith(".yaml") || a.EndsWith(".yml"))
                   ?? Path.Combine(AppContext.BaseDirectory, "workload.yaml");

// The two halves of the type system: what a workload may declare, and how this backend
// realises it. Both are data, loaded from YAML shipped beside their assemblies.
var core = ResourceTypeLoader.LoadDefault();
var backendTypes = BackendTypeLoader.LoadDefault();

var workload = WorkloadLoader.Load(await File.ReadAllTextAsync(workloadPath));
var plan = new WorkloadResolver(core, backendTypes).Resolve(workload);

Console.WriteLine($"workload : {workload.Metadata.Name} ({workload.Metadata.Environment}) v{plan.Version}");
Console.WriteLine($"resources: {string.Join(", ", plan.Resources.Select(Describe))}");
Console.WriteLine();
Console.WriteLine("=== plan ===");
foreach (var group in plan.Deployments.GroupBy(d => d.Depth))
{
    Console.WriteLine($"  depth {group.Key}   (may run concurrently)");
    foreach (var deployment in group)
    {
        var needs = deployment.DependsOn.Count > 0 ? $"  after {string.Join(", ", deployment.DependsOn)}" : "";
        Console.WriteLine($"    {deployment.Key,-28} {deployment.Definition,-22} -> {deployment.StackName}{needs}");
    }
}

if (planOnly)
{
    return;
}

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole());
services.RegisterPulumiBackend(options =>
{
    options.BackendUrl = "file://~";
    // The local backend still encrypts state and needs a passphrase non-interactively. An
    // empty one is for this sample only.
    options.ConfigPassPhrase = "";
});

var provider = services.BuildServiceProvider();

var executor = new WorkloadExecutor(
    provider.GetRequiredService<IBackEndProvider>(),
    new FileSystemDefinitionStore(Path.Combine(repositoryRoot, backendTypes.Shared.DefinitionsRoot)),
    backendTypes,
    new ExecutionOptions { Preview = preview });

Console.WriteLine();
Console.WriteLine($"=== {(preview ? "preview" : "deploy")} ===");
var result = await executor.ExecuteAsync(plan);

Console.WriteLine();
Console.WriteLine("=== deployments ===");
foreach (var outcome in result.Deployments)
{
    var status = outcome.Error is null
        ? string.Join(", ", outcome.Summary.Select(s => $"{s.Key}={s.Value}"))
        : $"FAILED: {outcome.Error}";
    Console.WriteLine($"  {outcome.Key,-28} {status}");
}

foreach (var skipped in result.Skipped)
{
    Console.WriteLine($"  {skipped,-28} skipped (a dependency failed)");
}

Console.WriteLine();
Console.WriteLine("=== resource outputs ===");
foreach (var (resource, outputs) in result.ResourceOutputs)
{
    Console.WriteLine($"  {resource}:");
    foreach (var (key, value) in outputs)
    {
        // Secret-marked outputs are masked: the contract says which are credential material.
        var secret = core.Get(plan.Resources.Single(r => r.Name == resource).Type).Outputs[key].Secret;
        Console.WriteLine($"    {key}: {(secret ? "<secret>" : value)}");
    }
}

Environment.ExitCode = result.Succeeded ? 0 : 1;

static string Describe(PlannedResource resource) =>
    $"{resource.Name}:{resource.Type}{(resource.PolicyAttached ? " (policy)" : "")}";

static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("Could not locate the repository root.");
}
