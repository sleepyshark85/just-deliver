using jd.core.bp;
using Xunit;

namespace jd.orchestrator.tests;

// What a node's report says changed, as a client lists it: every operation but "Same" (in any case), and only those with a count.
public sealed class NodeReportTests
{
    private static NodeReport Report(Dictionary<string, int> summary, params string[] operations) =>
        new("w/e/s/n", NodeOutcome.Deployed, summary,
            [.. operations.Select((operation, index) => new ResourceChange { Urn = $"urn{index}", Type = $"type{index}", Operation = operation })],
            string.Empty, TimeSpan.Zero, new Dictionary<string, ConfigEntry>());

    [Fact]
    public void The_changed_operations_are_the_summarys_operations_other_than_same_that_counted_a_resource()
    {
        var report = Report(new() { ["Create"] = 2, ["Same"] = 3, ["SAME"] = 1, ["Update"] = 0, ["delete"] = 1 });

        Assert.Equal([new("Create", 2), new KeyValuePair<string, int>("delete", 1)], report.ChangedOperations);
    }

    [Fact]
    public void The_changed_resources_are_the_changes_other_than_same_in_their_order()
    {
        var report = Report([], "Update", "Same", "same", "Create");

        Assert.Equal([("type0", "Update"), ("type3", "Create")], report.ChangedResources.Select(change => (change.Type, change.Operation)));
    }
}
