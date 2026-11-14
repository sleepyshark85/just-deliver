using System.Globalization;
using jd.resolver.expressions;

namespace jd.resolver.catalog;

/// <summary>
/// What a runtime's release block asks of its part (docs/architecture/documents.md section 2, concept 11). A runtime is deployed in steps (dark
/// revision, grants, probe, traffic shift): the probe is tried at most once per interval within its timeout, every revision suffix fits the
/// longest one the runtime accepts, and the engine owns the inputs the release block names, so the runtime node must not set them. The schema
/// requires the probe, the release block and the runtime node. Each error is located where the value it is about was read.
/// </summary>
internal static class RolloutRules
{
    /// <summary>
    /// Every rollout of <paramref name="rollouts"/> whose probe interval exceeds its timeout, then whose suffix parts exceed its suffix's longest
    /// length, then each release input its runtime node sets.
    /// </summary>
    public static IReadOnlyList<LoadError> Check(IEnumerable<Rollout> rollouts) =>
        [.. rollouts.SelectMany(rollout => CheckInterval(rollout).Concat(CheckSuffix(rollout)).Concat(CheckEngineInputs(rollout)))];

    private static IEnumerable<LoadError> CheckInterval(Rollout rollout)
    {
        if (rollout.Probe.IntervalSeconds > rollout.Probe.TimeoutSeconds)
        {
            yield return new LoadError(rollout.Runtime.Source, rollout.IntervalLocation, $"must not exceed {rollout.TimeoutName}.");
        }
    }

    // A sum no schema keyword states, so the runtime's data carries the bound and the rule is this generic one.
    private static IEnumerable<LoadError> CheckSuffix(Rollout rollout)
    {
        var suffix = rollout.Release.Suffix;
        if (suffix.Length > rollout.SuffixMaxLength)
        {
            yield return new LoadError(rollout.Runtime.Source, rollout.SuffixMaxLengthLocation, string.Create(CultureInfo.InvariantCulture,
                $"the suffix's prefix '{suffix.Prefix}' and its {suffix.ConfigDigits} + {suffix.SaltDigits} digits take {suffix.Length} characters, more than the {rollout.SuffixMaxLength} allowed."));
        }
    }

    private static IEnumerable<LoadError> CheckEngineInputs(Rollout rollout)
    {
        var node = rollout.Part.Nodes[ExpressionEvaluator.RuntimeNode];
        return new[] { rollout.Release.SuffixInput, rollout.Release.TrafficInput }.Where(node.Config.ContainsKey)
            .Select(input => new LoadError(rollout.Runtime.Source, node.Config[input].Path, "the release flow sets this input; the runtime node must not."));
    }
}
