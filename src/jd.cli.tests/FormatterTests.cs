using jd.core.bp;
using jd.orchestrator;
using jd.resolver.catalog;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using jd.resolver.policies;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.cli.tests;

// The formatters' own branches the command tests' fixtures do not reach: an unchanged resource beside a change, a config leaf without provenance.
public sealed class FormatterTests
{
    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    [Fact]
    public void A_deploy_line_counts_and_lists_the_changed_resources_only()
    {
        var report = new NodeReport("w/e/s/n", NodeOutcome.Deployed, new Dictionary<string, int> { ["Update"] = 1, ["Same"] = 2 },
            [new ResourceChange { Urn = "u1", Type = "t1", Operation = "Update" }, new ResourceChange { Urn = "u2", Type = "t2", Operation = "Same" }],
            string.Empty, TimeSpan.FromSeconds(1.5), new Dictionary<string, ConfigEntry>());

        Assert.Equal(Lines("w/e/s/n: deployed (update 1, 1.5s)", "  update t1"), DeployFormatter.Format(report));
    }

    [Fact]
    public void A_config_leaf_without_provenance_is_listed_as_such()
    {
        var node = new GraphNode("w/e/s/n", "s", "n", "template", NodeKind.Create, "w.e.s.n", Phase.Infrastructure,
            new Dictionary<string, ConfigValue> { ["size"] = new ConfigScalar(new JValue(3)) }, new Dictionary<string, Provenance>(), "0123456789abcdef", []);
        var graph = new ResolvedGraph("1.0.0", "e", "w", "team", null, null, [node], new Dictionary<string, IReadOnlyDictionary<string, EvalResult>>(), [],
            new Dictionary<string, IReadOnlyDictionary<string, string>>());

        Assert.EndsWith(Lines("  depends on: nothing", "  size = 3   [no provenance]"), PreviewFormatter.Format(graph));
    }
}
