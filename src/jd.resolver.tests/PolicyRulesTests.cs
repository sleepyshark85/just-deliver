using jd.resolver.catalog;
using Newtonsoft.Json.Linq;
using Xunit;

namespace jd.resolver.tests;

// The messages of the policy shape rules (PolicyRules) that depend on how many criteria the policy schema has.
public sealed class PolicyRulesTests
{
    private static readonly string[] Fields = ["alpha", "beta", "gamma"];

    [Theory]
    [InlineData(1, "alpha")]
    [InlineData(2, "alpha and beta")]
    [InlineData(3, "alpha, beta and gamma")]
    public async Task A_runtime_on_a_node_scope_policy_names_every_criterion_a_node_scope_policy_matches(int count, string listed)
    {
        var binder = await PlantedPolicySchema.BinderAsync([.. Fields.Take(count).Select(field => (field, "node.template"))]);
        var policy = binder.ReadPolicy(new Document("p.yaml", "Policy", JObject.Parse("""{ "kind": "Policy", "name": "pol", "reason": "r", "match": { "runtime": "x" }, "set": { "size": 1 } }""")));

        var error = Assert.Single(PolicyRules.Check([policy]));

        Assert.Equal($"'runtime' needs 'kind: runtime'; node-scope policies match {listed}.", error.Message);
    }
}
