using jd.core.bp;
using jd.core.revisions;
using jd.resolver.catalog;

namespace jd.orchestrator.tests;

// Answers each probe call from Answer (given the 1-based attempt number); records what it was asked and, in the shared log, when.
internal sealed class FakeProbe(List<string> log) : IRevisionProbe
{
    public List<(string Fqdn, Probe Probe)> Calls { get; } = [];
    public List<TimeSpan> Delays { get; } = [];
    public Func<int, ProbeResult> Answer { get; set; } = _ => new ProbeResult(true, "HTTP 200");

    public Task<ProbeResult> ProbeAsync(string fqdn, Probe probe, CancellationToken cancellationToken)
    {
        Calls.Add((fqdn, probe));
        log.Add("probe");
        return Task.FromResult(Answer(Calls.Count));
    }
}

// A fake backend that also plays the runtime template: it echoes the traffic it was given (the template's `traffic` output), names the
// revision app--<suffix> and answers with an fqdn, as the Container App does. With DetectChanges, a deploy with the parameters the
// stack already has reports no changes.
internal sealed class FakeBackend : IBackEndProvider, ITemplateStore
{
    private readonly Dictionary<string, Dictionary<string, string?>> _applied = [];

    public List<(string Op, DeploymentPackage Package)> Calls { get; } = [];
    public List<string> Log { get; } = [];
    public Dictionary<string, Dictionary<string, ConfigEntry>> State { get; } = [];
    public Dictionary<string, int> Summary { get; set; } = new() { ["Create"] = 1 };

    // What a preview of the stack reports, instead of Summary: a drifted stack has changes, a clean one only "Same".
    public Dictionary<string, Dictionary<string, int>> PreviewSummaries { get; } = [];
    public bool DetectChanges { get; set; }
    public string? FailOn { get; set; }
    public bool OmitIdOutput { get; set; }
    public string? Fqdn { get; set; } = "app.example.test";

    public string ContentVersion { get; set; } = string.Empty;

    public string GetContent(string templateName) => "template:" + templateName + ContentVersion;

    public Task<DeploymentResult> DeployAsync(DeploymentPackage package, CancellationToken cancellationToken)
    {
        Calls.Add(("deploy", package));
        Log.Add("deploy " + package.StackName);
        if (package.StackName == FailOn)
        {
            throw new InvalidOperationException("provider exploded");
        }

        var outputs = new Dictionary<string, ConfigEntry>
        {
            ["name"] = new("n-" + package.StackName),
            ["key"] = new("k-" + package.StackName, isSecret: true),
            ["principalId"] = new("p-" + package.StackName),
        };
        if (!OmitIdOutput)
        {
            outputs["id"] = new("id-" + package.StackName);
        }

        if (package.DeploymentParameters.TryGetValue("traffic", out var traffic) && package.DeploymentParameters.TryGetValue("suffix", out var suffix))
        {
            outputs["traffic"] = traffic;
            outputs["suffix"] = suffix;
            outputs["revision"] = new("app--" + suffix.Value);
            outputs["appId"] = new("/apps/" + package.StackName);
            if (Fqdn is not null)
            {
                outputs["fqdn"] = new(Fqdn);
            }
        }

        var applied = package.DeploymentParameters.ToDictionary(p => p.Key, p => p.Value.Value);
        var same = _applied.TryGetValue(package.StackName, out var before) && before.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(applied.OrderBy(p => p.Key, StringComparer.Ordinal));
        _applied[package.StackName] = applied;
        State[package.StackName] = outputs;
        return Task.FromResult(new DeploymentResult { Outputs = outputs, Summary = DetectChanges ? new() { [same ? "Same" : "Update"] = 1 } : Summary });
    }

    public Task<DeploymentResult> PreviewAsync(DeploymentPackage package, CancellationToken cancellationToken)
    {
        Calls.Add(("preview", package));
        Log.Add("preview " + package.StackName);
        return Task.FromResult(new DeploymentResult { Outputs = [], Summary = PreviewSummaries.GetValueOrDefault(package.StackName) ?? Summary });
    }

    public Task<Dictionary<string, ConfigEntry>?> GetOutputsAsync(string stackName, string deploymentContent, CancellationToken cancellationToken)
    {
        Calls.Add(("read", new DeploymentPackage { StackName = stackName, DeploymentContent = deploymentContent, DeploymentParameters = [] }));
        return Task.FromResult(State.GetValueOrDefault(stackName));
    }
}

// Answers the live revisions of every app from Answer; records which apps were asked about.
internal sealed class FakeLiveRevisions(Func<string, IReadOnlyList<LiveRevision>> answer) : ILiveRevisions
{
    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<LiveRevision>> GetAsync(string resourceId, CancellationToken cancellationToken)
    {
        Calls.Add(resourceId);
        return Task.FromResult(answer(resourceId));
    }
}
