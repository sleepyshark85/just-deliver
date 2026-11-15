using jd.resolver.catalog;
using jd.resolver.expansion;
using jd.resolver.expressions;
using jd.resolver.graph;
using jd.resolver.policies;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// A node's config leaves, as a client lists them: ordinal path order, objects walked into, each leaf with its provenance's one-line form and its
// canonical JSON.
public sealed class ConfigLeafTests
{
    private static GraphNode Node(Dictionary<string, ConfigValue> config, Dictionary<string, Provenance> provenance) =>
        new("w/e/s/n", "s", "n", "template", new SourceLocation("c.yaml", "nodes.n.template"), NodeKind.Create, "w.e.s.n", Phase.Infrastructure, config, provenance, "hash", []);

    [Fact]
    public void A_nodes_leaves_are_in_ordinal_path_order_each_with_its_provenance_or_none()
    {
        var size = new ConfigScalar(new JValue(3));
        var level = new ConfigText(new Resolved("Session"), new HashSet<string>());
        var tags = new ConfigArray([new ConfigScalar(new JValue("a"))]);
        var node = Node(
            new() { ["size"] = size, ["consistency"] = new ConfigObject(new Dictionary<string, ConfigValue> { ["level"] = level }), ["Tags"] = tags },
            new() { ["consistency.level"] = new Provenance("policies/p.yaml", "p", Layer.PolicySet), ["size"] = new Provenance("c.yaml", "c.yaml", Layer.Implementation) });

        Assert.Equal(
            [
                new ConfigLeaf("Tags", tags, null),
                new ConfigLeaf("consistency.level", level, "PolicySet: p (policies/p.yaml)"),
                new ConfigLeaf("size", size, "Implementation: c.yaml"),
            ],
            node.Leaves);
    }

    [Fact]
    public void A_leafs_json_is_its_canonical_form()
    {
        var leaf = new ConfigLeaf("tags", new ConfigArray([new ConfigScalar(new JValue(1)), new ConfigText(new Pending("${a.b}", new HashSet<Reference>()), new HashSet<string>())]), null);

        Assert.Equal("""[1,"${a.b}"]""", leaf.Json.ToString(Newtonsoft.Json.Formatting.None));
    }
}
