using jd.core.bp;
using jd.definitionvalidator;
using jd.orchestrator;
using jd.resolver.catalog;
using Newtonsoft.Json.Linq;

namespace jd.cli.tests;

// A fake backend and probe: records each stack it is asked to deploy or preview and each probe, fails the stack named in FailOn, and
// answers every output the template declares with "out-<name>" so the nodes that read them (and the release flow) can go on.
// The probe passes unless ProbePasses is false.
internal sealed class RecordingBackend : IBackEndProvider, IRevisionProbe
{
    public List<string> Calls { get; } = [];
    public string? FailOn { get; set; }
    public bool ProbePasses { get; set; } = true;

    public Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken)
    {
        Calls.Add("deploy " + package.StackName);
        var outputs = ((YamlSchemaValidator.ParseYaml(package.DeploymentContent) as JObject)?["outputs"] as JObject)?.Properties()
            .ToDictionary(p => p.Name, p => new ConfigEntry("out-" + p.Name)) ?? [];
        return package.StackName == FailOn
            ? throw new InvalidOperationException("provider exploded")
            : Task.FromResult(new DeploymentResult { Outputs = outputs, Summary = new() { ["Create"] = 1 }, Changes = [new ResourceChange { Urn = "urn", Type = "azure-native:t:T", Operation = "create" }] });
    }

    public Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken)
    {
        Calls.Add("preview " + package.StackName);
        return Task.FromResult(new DeploymentResult { Outputs = [], Summary = new() { ["Create"] = 1 } });
    }

    public Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string stackName, string deploymentContent, CancellationToken cancellationToken) =>
        Task.FromResult<Dictionary<string, ConfigEntry>?>(null);

    public Task<ProbeResult> ProbeAsync(string fqdn, Probe probe, CancellationToken cancellationToken)
    {
        Calls.Add($"probe {fqdn}{probe.Path}");
        return Task.FromResult(new ProbeResult(ProbePasses, ProbePasses ? "HTTP 200" : "HTTP 503"));
    }
}
