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
/// of the new revision, and the traffic shift. A revision that fails its probe never gets traffic while a live one exists. The mapping's
/// release block names the template's inputs and outputs and the shape of a traffic item, so none is in code. <c>prepare</c> fills the
/// node's config as for any node; <c>deployDependants</c> deploys the nodes that depend on the runtime, given the dark deploy's outputs, and
/// returns the failure reason or null. Rules: docs/architecture/provisioning.md.
/// </summary>
internal sealed class ReleaseFlow(
    IBackEndProvider provider, IRevisionProbe probe, Func<TimeSpan, CancellationToken, Task> delay, bool preview,
    Func<GraphNode, Task<Prepared>> prepare, Func<Dictionary<string, ConfigEntry>, Task<string?>> deployDependants)
{
    // Azure's rule for a revision suffix: lowercase letters, digits and '-', starting with a letter; the revision name <app>--<suffix>
    // takes at most 54 characters. The suffix is a letter and 10 hex digits.
    private const string SuffixPrefix = "r";
    private const int SuffixHexLength = 10;
    private const int FullWeight = 100;

    public async Task<NodeReport> RunAsync(GraphNode node, CancellationToken cancellationToken)
    {
        var release = node.Release ?? throw new InvalidOperationException($"node '{node.Id}': the runtime mapping has no release block.");
        var spec = node.Probe ?? throw new InvalidOperationException($"node '{node.Id}': the runtime mapping has no probe.");
        var steps = new List<StepReport>();
        var step = Step.Dark;
        DeploymentResult? dark = null;
        NodeReport Stop(string reason)
        {
            steps.Add(new StepReport(step, StepOutcome.Failed, reason));
            return NodeReport.For(node, NodeOutcome.Failed, $"{step.ToString().ToLowerInvariant()}: {reason}", dark, steps);
        }

        try
        {
            var prepared = await prepare(node);
            if (prepared.Package is not { } package)
            {
                return prepared.Outcome == NodeOutcome.Failed ? Stop(prepared.Message) : NodeReport.For(node, prepared.Outcome, prepared.Message);
            }

            // The engine alone sets the suffix and the traffic; the same config gives the same suffix.
            var suffix = new ConfigEntry(Suffix(package.DeploymentParameters, release));
            DeploymentPackage With(ConfigEntry traffic) => new()
            {
                StackName = package.StackName,
                DeploymentContent = package.DeploymentContent,
                DeploymentParameters = new Dictionary<string, ConfigEntry>(package.DeploymentParameters) { [release.SuffixInput] = suffix, [release.TrafficInput] = traffic },
            };

            var live = LiveRevision(await provider.GetOutputsAsync(node.Stack, package.DeploymentContent, cancellationToken), release);
            var darkPackage = With(Traffic(release, live));
            if (preview)
            {
                var planned = await provider.PreviewAsync(darkPackage, cancellationToken);
                return NodeReport.For(node, NodeOutcome.Previewed, string.Empty, planned, Enum.GetValues<Step>().Select(s => new StepReport(s, StepOutcome.Planned, string.Empty)).ToList());
            }

            dark = await provider.DeployAsync(darkPackage, cancellationToken);
            var revision = dark.Outputs.GetValueOrDefault(release.RevisionOutput)?.Value;
            if (string.IsNullOrEmpty(revision))
            {
                return Stop($"the deploy exported no '{release.RevisionOutput}'.");
            }

            steps.Add(new StepReport(Step.Dark, StepOutcome.Done, revision));

            step = Step.Grants;
            if (await deployDependants(dark.Outputs) is { } reason)
            {
                return Stop(reason);
            }

            steps.Add(new StepReport(Step.Grants, StepOutcome.Done, string.Empty));

            // Same config and the live revision is the one just deployed: nothing new to check or shift.
            if (!dark.HasChanges && revision == live)
            {
                steps.Add(new StepReport(Step.Probe, StepOutcome.Skipped, string.Empty));
                steps.Add(new StepReport(Step.Shift, StepOutcome.Skipped, string.Empty));
                return NodeReport.For(node, NodeOutcome.Unchanged, string.Empty, dark, steps);
            }

            step = Step.Probe;
            var fqdn = dark.Outputs.GetValueOrDefault(release.FqdnOutput)?.Value;
            if (string.IsNullOrEmpty(fqdn))
            {
                return Stop($"the deploy exported no '{release.FqdnOutput}' to probe.");
            }

            var result = await ProbeAsync(fqdn, spec, cancellationToken);
            if (!result.Passed)
            {
                return Stop(result.Detail);
            }

            steps.Add(new StepReport(Step.Probe, StepOutcome.Done, result.Detail));

            step = Step.Shift;
            if (live is null)
            {
                steps.Add(new StepReport(Step.Shift, StepOutcome.Skipped, "first deploy: the latest revision already has all traffic"));
            }
            else
            {
                await provider.DeployAsync(With(Traffic(release, revision)), cancellationToken);
                steps.Add(new StepReport(Step.Shift, StepOutcome.Done, revision));
            }

            return NodeReport.For(node, NodeOutcome.Deployed, string.Empty, dark, steps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Stop(ex.Message);
        }
    }

    // The probe is retried every interval until the timeout, so grants that take minutes to propagate do not fail a healthy revision.
    private async Task<ProbeResult> ProbeAsync(string fqdn, Probe spec, CancellationToken cancellationToken)
    {
        var attempts = 1 + spec.TimeoutSeconds / spec.IntervalSeconds;
        for (var attempt = 1; ; attempt++)
        {
            var result = await probe.ProbeAsync(fqdn, spec, cancellationToken);
            if (result.Passed)
            {
                return result;
            }

            if (attempt == attempts)
            {
                return result with { Detail = $"{result.Detail} after {attempts.ToString(CultureInfo.InvariantCulture)} attempts" };
            }

            await delay(TimeSpan.FromSeconds(spec.IntervalSeconds), cancellationToken);
        }
    }

    // Hashes the filled config, secret flags included. The engine's own inputs are not config and do not count.
    private static string Suffix(Dictionary<string, ConfigEntry> parameters, RuntimeRelease release)
    {
        var canonical = new JArray(parameters
            .Where(p => p.Key != release.SuffixInput && p.Key != release.TrafficInput)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new JArray(p.Key, p.Value.Value ?? string.Empty, p.Value.IsSecret)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString(Formatting.None)));
        return SuffixPrefix + Convert.ToHexStringLower(hash)[..SuffixHexLength];
    }

    // The live revision is the last revision the traffic names; no outputs, or only the latest revision, is a first deploy.
    private static string? LiveRevision(Dictionary<string, ConfigEntry>? outputs, RuntimeRelease release) =>
        outputs?.GetValueOrDefault(release.TrafficOutput)?.Value is { } traffic
            ? JArray.Parse(traffic).OfType<JObject>().Select(item => (string?)item[release.RevisionKey]).LastOrDefault(name => name is not null)
            : null;

    // All traffic to the named revision, or to the latest one when none is named (a first deploy).
    private static ConfigEntry Traffic(RuntimeRelease release, string? revision) =>
        new(new JArray(new JObject(
            revision is null ? new JProperty(release.LatestKey, true) : new JProperty(release.RevisionKey, revision),
            new JProperty(release.WeightKey, FullWeight))).ToString(Formatting.None));
}
