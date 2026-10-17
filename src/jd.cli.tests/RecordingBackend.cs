using jd.core.bp;
using jd.definitionvalidator;
using jd.orchestrator;
using jd.resolver.catalog;
using Newtonsoft.Json.Linq;

namespace jd.cli.tests;

// A fake backend and probe: records each stack it is asked to deploy or preview and each probe, fails the stack named in FailOn, and
// answers every output the template declares with "out-<name>" so the nodes that read them (and the release flow) can go on.
// The probe passes unless ProbePasses is false. With Deployed, every stack looks deployed and clean; so does a stack this backend deployed,
// as after a deploy: a preview finds no change and the state has the outputs the templates declare (what a qualification reads); the traffic is valid JSON
// naming a revision, as the release flow reads it. PreviewDelay makes each preview take that long; the previews of the stacks in PreviewDrift find a change.
internal sealed class RecordingBackend : IBackEndProvider, IRevisionProbe
{
    public List<string> Calls { get; } = [];
    public string? FailOn { get; set; }
    public bool ProbePasses { get; set; } = true;
    public bool Deployed { get; set; }
    public TimeSpan PreviewDelay { get; set; }
    public HashSet<string> PreviewDrift { get; } = [];
    private readonly HashSet<string> _deployedStacks = [];

    public Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken)
    {
        Calls.Add("deploy " + package.StackName);
        var outputs = OutputsOf(package.DeploymentContent);
        if (package.StackName == FailOn)
        {
            throw new InvalidOperationException("provider exploded");
        }

        _deployedStacks.Add(package.StackName);
        return Task.FromResult(new DeploymentResult { Outputs = outputs, Summary = new() { ["Create"] = 1 }, Changes = [new ResourceChange { Urn = "urn", Type = "azure-native:t:T", Operation = "create" }] });
    }

    public async Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken)
    {
        Calls.Add("preview " + package.StackName);
        await Task.Delay(PreviewDelay, cancellationToken);
        var operation = PreviewDrift.Contains(package.StackName) ? "Update" : IsDeployed(package.StackName) ? "Same" : "Create";
        return new DeploymentResult { Outputs = [], Summary = new() { [operation] = 1 } };
    }

    public Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string stackName, string deploymentContent, CancellationToken cancellationToken) =>
        Task.FromResult(IsDeployed(stackName) ? OutputsOf(deploymentContent) : null);

    private bool IsDeployed(string stack) => Deployed || _deployedStacks.Contains(stack);

    private static Dictionary<string, ConfigEntry> OutputsOf(string deploymentContent) =>
        ((YamlSchemaValidator.ParseYaml(deploymentContent) as JObject)?["outputs"] as JObject)?.Properties()
            .ToDictionary(p => p.Name, p => new ConfigEntry(p.Name == "traffic" ? """[{"revisionName":"app--r1","weight":100}]""" : "out-" + p.Name)) ?? [];

    public Task<ProbeResult> ProbeAsync(string fqdn, Probe probe, CancellationToken cancellationToken)
    {
        Calls.Add($"probe {fqdn}{probe.Path}");
        return Task.FromResult(new ProbeResult(ProbePasses, ProbePasses ? "HTTP 200" : "HTTP 503"));
    }
}
