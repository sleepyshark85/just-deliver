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
/// release block names the template's inputs and outputs and the shape of a traffic item, so none is in code. <c>prepare</c> fills the
/// node's config as for any node; <c>deployDependants</c> deploys the nodes that depend on the runtime, given the dark deploy's outputs, and
/// returns the failure reason or null. Rules: docs/architecture/provisioning.md.
/// </summary>
internal sealed class ReleaseFlow(
    IBackEndProvider provider, IRevisionProbe probe, Func<TimeSpan, CancellationToken, Task> delay, bool preview,
    Func<GraphNode, Task<Prepared>> prepare, Func<Dictionary<string, ConfigEntry>, Task<string?>> deployDependants)
{
    // Azure's rule for a revision suffix: lowercase letters, digits and '-', starting with a letter; the revision name <app>--<suffix>
    // takes at most 54 characters. The suffix is a letter, 8 hex digits of the config and 6 of the salt: 15 characters.
    private const string SuffixPrefix = "r";
    private const int ConfigHexLength = 8;
    private const int SaltHexLength = 6;
    private const int FullWeight = 100;

    public async Task<NodeReport> RunAsync(GraphNode node, CancellationToken cancellationToken)
    {
        var release = node.Release ?? throw new InvalidOperationException($"node '{node.Id}': the runtime has no release block.");
        var spec = node.Probe ?? throw new InvalidOperationException($"node '{node.Id}': the runtime has no probe.");
        var steps = new List<StepReport>();
        var step = Step.Dark;
        DeploymentResult? dark = null;
        // Each step is timed from the end of the one before (the first from the start of the flow), so the steps never add up to more than the node.
        var clock = Stopwatch.StartNew();
        StepReport Report(Step reported, StepOutcome outcome, string detail, int attempts = 0)
        {
            var report = new StepReport(reported, outcome, detail, clock.Elapsed, attempts);
            clock.Restart();
            return report;
        }

        NodeReport Stop(string reason, int attempts = 0)
        {
            steps.Add(Report(step, StepOutcome.Failed, reason, attempts));
            return NodeReport.For(node, NodeOutcome.Failed, $"{step.ToString().ToLowerInvariant()}: {reason}", dark, steps);
        }

        try
        {
            var prepared = await prepare(node);
            if (prepared.Package is not { } package)
            {
                return prepared.Outcome == NodeOutcome.Failed ? Stop(prepared.Message) : NodeReport.For(node, prepared.Outcome, prepared.Message);
            }

            // The engine alone sets the suffix and the traffic. The same config as the last deploy keeps its suffix; otherwise a new one.
            var outputs = await provider.GetOutputsAsync(node.Stack, package.DeploymentContent, cancellationToken);
            var suffix = new ConfigEntry(Suffix(package, release, outputs?.GetValueOrDefault(release.SuffixOutput)?.Value ?? string.Empty));
            DeploymentPackage With(ConfigEntry traffic) => new()
            {
                StackName = package.StackName,
                DeploymentContent = package.DeploymentContent,
                DeploymentParameters = new Dictionary<string, ConfigEntry>(package.DeploymentParameters) { [release.SuffixInput] = suffix, [release.TrafficInput] = traffic },
            };

            var live = LiveRevision(outputs, release);
            var darkPackage = With(Traffic(release, live));
            if (preview)
            {
                var planned = await provider.PreviewAsync(darkPackage, cancellationToken);
                // The grants are previewed with the runtime's outputs from state; a runtime never deployed has none, so they wait on it.
                step = Step.Grants;
                return await deployDependants(outputs ?? []) is { } blocked
                    ? Stop(blocked)
                    : NodeReport.For(node, NodeOutcome.Previewed, string.Empty, planned, Enum.GetValues<Step>().Select(s => new StepReport(s, StepOutcome.Planned, string.Empty)).ToList());
            }

            dark = await provider.DeployAsync(darkPackage, cancellationToken);
            var revision = dark.Outputs.GetValueOrDefault(release.RevisionOutput)?.Value;
            if (string.IsNullOrEmpty(revision))
            {
                return Stop($"the deploy exported no '{release.RevisionOutput}'.");
            }

            steps.Add(Report(Step.Dark, StepOutcome.Done, revision));

            step = Step.Grants;
            if (await deployDependants(dark.Outputs) is { } reason)
            {
                return Stop(reason);
            }

            steps.Add(Report(Step.Grants, StepOutcome.Done, string.Empty));

            // Same config and the live revision is the one just deployed: nothing new to check or shift.
            if (!dark.HasChanges && revision == live)
            {
                steps.Add(Report(Step.Probe, StepOutcome.Skipped, string.Empty));
                steps.Add(Report(Step.Shift, StepOutcome.Skipped, string.Empty));
                return NodeReport.For(node, NodeOutcome.Unchanged, string.Empty, dark, steps);
            }

            step = Step.Probe;
            var fqdn = dark.Outputs.GetValueOrDefault(release.FqdnOutput)?.Value;
            if (string.IsNullOrEmpty(fqdn))
            {
                return Stop($"the deploy exported no '{release.FqdnOutput}' to probe.");
            }

            var (result, attempts) = await ProbeAsync(fqdn, spec, cancellationToken);
            if (!result.Passed)
            {
                return Stop(result.Detail, attempts);
            }

            steps.Add(Report(Step.Probe, StepOutcome.Done, result.Detail, attempts));

            step = Step.Shift;
            // Also on a first deploy: it makes the stack's traffic name the revision, which is how the next run finds the live one.
            var shifted = await provider.DeployAsync(With(Traffic(release, revision)), cancellationToken);
            steps.Add(Report(Step.Shift, StepOutcome.Done, revision));

            // The last deploy's outputs name the live revision (the traffic), so the persisted report carries them.
            return NodeReport.For(node, NodeOutcome.Deployed, string.Empty, shifted, steps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Stop(ex.Message);
        }
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
        var config = SuffixPrefix + Hash(canonical.ToString(Formatting.None))[..ConfigHexLength];
        return previous.StartsWith(config, StringComparison.Ordinal) ? previous : config + Hash(previous)[..SaltHexLength];
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
            new JProperty(release.WeightKey, FullWeight))).ToString(Formatting.None));
}
