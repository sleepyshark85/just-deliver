using jd.resolver.expressions;

namespace jd.resolver.catalog;

/// <summary>
/// What a runtime's release block asks of its part (docs/architecture/documents.md section 2, concept 11). A runtime is deployed in steps (dark
/// revision, grants, probe, traffic shift): the probe is tried at most once per interval within its timeout, and the engine owns the inputs the
/// release block names, so the runtime node must not set them. The schema requires the probe, the release block and the runtime node. Each error
/// is located where the probe's interval or the node's config was read.
/// </summary>
internal static class RolloutRules
{
    /// <summary>Every rollout of <paramref name="rollouts"/> whose probe interval exceeds its timeout, then each release input its runtime node sets.</summary>
    public static IReadOnlyList<LoadError> Check(IEnumerable<Rollout> rollouts) =>
        [.. rollouts.SelectMany(rollout =>
        {
            var source = rollout.Runtime.Source;
            var node = rollout.Part.Nodes[ExpressionEvaluator.RuntimeNode];
            IEnumerable<LoadError> interval = rollout.Probe.IntervalSeconds > rollout.Probe.TimeoutSeconds
                ? [new LoadError(source, rollout.IntervalLocation, "must not exceed timeoutSeconds.")]
                : [];
            return interval.Concat(new[] { rollout.Release.SuffixInput, rollout.Release.TrafficInput }.Where(node.Config.ContainsKey)
                .Select(input => new LoadError(source, $"{node.ConfigLocation}.{input}", "the release flow sets this input; the runtime node must not.")));
        })];
}
