using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using jd.core.bp;
using jd.resolver.catalog;
using jd.resolver.graph;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace jd.orchestrator;

/// <summary>What the walk hands the release flow: the node's package, or why there is none (a failure; in a preview also a value that waits on an upstream node).</summary>
internal sealed record Prepared(DeploymentPackage? Package, NodeOutcome Outcome = NodeOutcome.Failed, string Message = "");

/// <summary>
/// Deploys the runtime node in the steps of the release flow: a dark revision (the live revision keeps all traffic), the grants, a probe
/// of the new revision, and the traffic shift. A revision that fails its probe never gets traffic while a live one exists. The runtime's
/// release block names the template's inputs and outputs, the shape of a traffic item, how a revision suffix is made and the weight of all
/// traffic, so none is in code. <c>prepare</c> fills the node's config as for any node; <c>deployDependants</c> deploys the nodes that
/// depend on the runtime, given the dark deploy's outputs, and returns the failure reason or null. Rules: docs/architecture/provisioning.md.
/// </summary>
internal sealed class ReleaseFlow(
    IBackEndProvider provider, IRevisionProbe probe, Func<TimeSpan, CancellationToken, Task> delay,
    Func<GraphNode, Task<Prepared>> prepare, Func<Dictionary<string, ConfigEntry>, Task<string?>> deployDependants)
{
    /// <summary>Previews the dark deploy and the grants; every step of the flow is reported planned.</summary>
    public Task<NodeReport> PreviewAsync(GraphNode node, CancellationToken cancellationToken) => RunAsync(node, PreviewStepsAsync, cancellationToken);

    /// <summary>Runs the steps of the flow, stopping at the first that fails.</summary>
    public Task<NodeReport> DeployAsync(GraphNode node, CancellationToken cancellationToken) => RunAsync(node, DeployStepsAsync, cancellationToken);

    // Prepares the node's package and its release, then runs the steps; any failure fails the step in progress.
    private async Task<NodeReport> RunAsync(GraphNode node, Func<Candidate, StepLog, CancellationToken, Task<NodeReport>> steps, CancellationToken cancellationToken)
    {
        var release = node.Release ?? throw new InvalidOperationException($"node '{node.Id}': the runtime has no release block.");
        var spec = node.Probe ?? throw new InvalidOperationException($"node '{node.Id}': the runtime has no probe.");
        var log = new StepLog(node);
        try
        {
            var prepared = await prepare(node);
            if (prepared.Package is not { } package)
            {
                return prepared.Outcome == NodeOutcome.Failed ? log.Stop(prepared.Message) : NodeReport.For(node, prepared.Outcome, prepared.Message);
            }

            var outputs = await provider.GetOutputsAsync(node.Stack, package.DeploymentContent, cancellationToken);
            return await steps(new Candidate(release, spec, package, outputs), log, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) // reporting boundary: any failed step stops the release with a failed report
        {
            return log.Stop(ex.Message);
        }
    }

    // The grants are previewed with the runtime's outputs from state; a runtime never deployed has none, so they wait on it.
    private async Task<NodeReport> PreviewStepsAsync(Candidate candidate, StepLog log, CancellationToken cancellationToken)
    {
        var planned = await provider.PreviewAsync(candidate.Routing(candidate.Live), cancellationToken);
        log.Start(Step.Grants);
        return await deployDependants(candidate.Outputs ?? []) is { } blocked ? log.Stop(blocked) : log.Planned(planned);
    }

    // The dark deploy, the grants, and, unless the live revision is the one just deployed, the probe and the shift.
    private async Task<NodeReport> DeployStepsAsync(Candidate candidate, StepLog log, CancellationToken cancellationToken)
    {
        var dark = log.Dark = await provider.DeployAsync(candidate.Routing(candidate.Live), cancellationToken);
        var revision = dark.Outputs.GetValueOrDefault(candidate.Release.RevisionOutput)?.Value;
        if (string.IsNullOrEmpty(revision))
        {
            return log.Stop($"the deploy exported no '{candidate.Release.RevisionOutput}'.");
        }

        log.Add(Step.Dark, StepOutcome.Done, revision);
        if (await GrantsAsync(dark, log) is { } stopped)
        {
            return stopped;
        }

        // Same config and the live revision is the one just deployed: nothing new to check or shift.
        if (!dark.HasChanges && revision == candidate.Live)
        {
            log.Add(Step.Probe, StepOutcome.Skipped, string.Empty);
            log.Add(Step.Shift, StepOutcome.Skipped, string.Empty);
            return log.Report(NodeOutcome.Unchanged, dark);
        }

        return await ProbeAndShiftAsync(candidate, dark, revision, log, cancellationToken);
    }

    // The nodes that depend on the runtime, deployed with the dark deploy's outputs; the report that stops the flow, or null to go on.
    private async Task<NodeReport?> GrantsAsync(DeploymentResult dark, StepLog log)
    {
        log.Start(Step.Grants);
        if (await deployDependants(dark.Outputs) is { } reason)
        {
            return log.Stop(reason);
        }

        log.Add(Step.Grants, StepOutcome.Done, string.Empty);
        return null;
    }

    // Traffic goes to the new revision only once its probe passes.
    private async Task<NodeReport> ProbeAndShiftAsync(Candidate candidate, DeploymentResult dark, string revision, StepLog log, CancellationToken cancellationToken)
    {
        log.Start(Step.Probe);
        var fqdn = dark.Outputs.GetValueOrDefault(candidate.Release.FqdnOutput)?.Value;
        if (string.IsNullOrEmpty(fqdn))
        {
            return log.Stop($"the deploy exported no '{candidate.Release.FqdnOutput}' to probe.");
        }

        var (result, attempts) = await ProbeAsync(fqdn, candidate.Probe, cancellationToken);
        if (!result.Passed)
        {
            return log.Stop(result.Detail, attempts);
        }

        log.Add(Step.Probe, StepOutcome.Done, result.Detail, attempts);

        log.Start(Step.Shift);
        // Also on a first deploy: it makes the stack's traffic name the revision, which is how the next run finds the live one.
        var shifted = await provider.DeployAsync(candidate.Routing(revision), cancellationToken);
        log.Add(Step.Shift, StepOutcome.Done, revision);

        // The last deploy's outputs name the live revision (the traffic), so the persisted report carries them.
        return log.Report(NodeOutcome.Deployed, shifted);
    }

    // The probe is retried every interval until the timeout, so grants that take minutes to propagate do not fail a healthy revision.
    // Returns the last answer and how many attempts were made.
    private async Task<(ProbeResult Result, int Attempts)> ProbeAsync(string fqdn, Probe spec, CancellationToken cancellationToken)
    {
        var attempts = 1 + spec.TimeoutSeconds / spec.IntervalSeconds;
        for (var attempt = 1; ; attempt++)
        {
            var result = await probe.ProbeAsync(fqdn, spec, cancellationToken);
            if (result.Passed)
            {
                return (result, attempt);
            }

            if (attempt == attempts)
            {
                return (result with { Detail = $"{result.Detail} after {attempts.ToString(CultureInfo.InvariantCulture)} attempts" }, attempt);
            }

            await delay(TimeSpan.FromSeconds(spec.IntervalSeconds), cancellationToken);
        }
    }

    // The config part hashes the template and the filled config, secret flags included; the engine's own inputs are not config. Azure
    // rejects a suffix that names an existing revision, so the same config as the previous deploy reuses its suffix (it is that revision),
    // and any other config, a revert included, gets a salt derived from the previous suffix. Residual: a 24-bit salt collision on a
    // config deployed before names an existing revision, which Azure rejects at the dark step. So does a same-config re-run after a dark
    // deploy that failed once Azure had created the revision (an image that cannot be pulled, a transient error): the outputs keep the old
    // suffix, so the suffix repeats until the config changes. Traffic is unaffected.
    private static string Suffix(DeploymentPackage package, RuntimeRelease release, string previous)
    {
        var canonical = new JArray(package.DeploymentContent, new JArray(package.DeploymentParameters
            .Where(p => p.Key != release.SuffixInput && p.Key != release.TrafficInput)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new JArray(new JValue(p.Key), new JValue(p.Value.Value), new JValue(p.Value.IsSecret)))));
        var config = release.Suffix.Prefix + Hash(canonical.ToString(Formatting.None))[..release.Suffix.ConfigDigits];
        return previous.StartsWith(config, StringComparison.Ordinal) ? previous : config + Hash(previous)[..release.Suffix.SaltDigits];
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // The live revision is the last revision the traffic names; no outputs, or only the latest revision, is a first deploy.
    private static string? LiveRevision(Dictionary<string, ConfigEntry>? outputs, RuntimeRelease release) =>
        outputs?.GetValueOrDefault(release.TrafficOutput)?.Value is { } traffic
            ? JArray.Parse(traffic).OfType<JObject>().Select(item => (string?)item[release.RevisionKey]).LastOrDefault(name => !string.IsNullOrEmpty(name))
            : null;

    // All traffic to the named revision, or to the latest one when none is named (a first deploy).
    private static ConfigEntry Traffic(RuntimeRelease release, string? revision) =>
        new(new JArray(new JObject(
            revision is null ? new JProperty(release.LatestKey, true) : new JProperty(release.RevisionKey, revision),
            new JProperty(release.WeightKey, release.FullWeight))).ToString(Formatting.None));

    // The release of one prepared package, given the stack's last outputs (none before a first deploy). The engine alone sets the suffix
    // and the traffic: the same config as the last deploy keeps its suffix, otherwise a new one.
    private sealed record Candidate(RuntimeRelease Release, Probe Probe, DeploymentPackage Package, Dictionary<string, ConfigEntry>? Outputs)
    {
        private readonly ConfigEntry _suffix = new(Suffix(Package, Release, Outputs?.GetValueOrDefault(Release.SuffixOutput)?.Value ?? string.Empty));

        // The revision that has all traffic now; null on a first deploy.
        public string? Live { get; } = LiveRevision(Outputs, Release);

        // The package with the engine's suffix and all traffic to the named revision (the latest when null).
        public DeploymentPackage Routing(string? revision) => new()
        {
            StackName = Package.StackName,
            DeploymentContent = Package.DeploymentContent,
            DeploymentParameters = new Dictionary<string, ConfigEntry>(Package.DeploymentParameters) { [Release.SuffixInput] = _suffix, [Release.TrafficInput] = Traffic(Release, revision) },
        };
    }

    // The node's steps as they are reported. Each step is timed from the end of the one before (the first from the start of the flow), so
    // the steps never add up to more than the node.
    private sealed class StepLog(GraphNode node)
    {
        private readonly List<StepReport> _steps = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Step _current = Step.Dark;

        // The dark deploy once it ran, which a failure after it reports.
        public DeploymentResult? Dark { get; set; }

        // The step a failure from now on is reported against.
        public void Start(Step step) => _current = step;

        public void Add(Step step, StepOutcome outcome, string detail, int attempts = 0)
        {
            _steps.Add(new StepReport(step, outcome, detail, _clock.Elapsed, attempts));
            _clock.Restart();
        }

        // Fails the step in progress, which ends the flow.
        public NodeReport Stop(string reason, int attempts = 0)
        {
            Add(_current, StepOutcome.Failed, reason, attempts);
            return NodeReport.For(node, NodeOutcome.Failed, $"{_current.ToString().ToLowerInvariant()}: {reason}", Dark, _steps);
        }

        public NodeReport Report(NodeOutcome outcome, DeploymentResult result) => NodeReport.For(node, outcome, string.Empty, result, _steps);

        // A preview runs no step: each is planned.
        public NodeReport Planned(DeploymentResult planned) =>
            NodeReport.For(node, NodeOutcome.Previewed, string.Empty, planned, Enum.GetValues<Step>().Select(step => new StepReport(step, StepOutcome.Planned, string.Empty)).ToList());
    }
}
