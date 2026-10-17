using jd.resolver.environment;
using Xunit;

namespace jd.resolver.tests;

// The two pure rules of docs/architecture/approvals.md section 3 over object ids.
public class ApprovalPolicyTests
{
    private const string Alice = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Bob = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Carol = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    private static ApprovalPolicy Policy(int required = 1, bool separate = true) => new("dev", required, [Alice, Bob], separate);

    [Fact]
    public void A_principal_that_is_not_listed_does_not_count()
    {
        Assert.False(Policy().Counts(Carol, []));
        Assert.True(Policy().Counts(Alice, []));
    }

    [Fact]
    public void A_deployer_does_not_count_when_separation_is_on_and_does_when_it_is_off()
    {
        Assert.False(Policy(separate: true).Counts(Alice, [Alice]));
        Assert.True(Policy(separate: false).Counts(Alice, [Alice]));
        Assert.True(Policy(separate: true).Counts(Bob, [Alice]));
    }

    [Fact]
    public void Ids_compare_without_regard_to_case()
    {
        Assert.True(Policy().Counts(Alice.ToUpperInvariant(), []));
        Assert.False(Policy().Counts(Alice.ToUpperInvariant(), [Alice]));
        Assert.False(Policy(required: 2).Evaluate([Alice, Alice.ToUpperInvariant()], []));
    }

    [Fact]
    public void The_same_principal_approving_twice_counts_once()
    {
        Assert.True(Policy(required: 1).Evaluate([Alice, Alice], []));
        Assert.False(Policy(required: 2).Evaluate([Alice, Alice], []));
    }

    [Fact]
    public void Required_distinct_approvers_are_needed()
    {
        Assert.False(Policy(required: 2).Evaluate([Alice], []));
        Assert.True(Policy(required: 2).Evaluate([Alice, Bob], []));
    }

    [Fact]
    public void Only_approvals_that_count_are_counted()
    {
        Assert.False(Policy(required: 2).Evaluate([Alice, Carol], []));
        Assert.False(Policy(required: 2).Evaluate([Alice, Bob], [Bob]));
        Assert.False(Policy(required: 1).Evaluate([], []));
    }
}
