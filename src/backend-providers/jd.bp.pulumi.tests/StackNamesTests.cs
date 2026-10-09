using Xunit;

namespace jd.bp.pulumi.tests;

public class StackNamesTests
{
    [Theory]
    [InlineData("shop.dev.orders.database")]
    [InlineData("shop.dev._workload.appinsights")]
    public void A_short_name_passes_through_unchanged(string name) =>
        Assert.Equal(name, StackNames.ToPulumi(name));

    [Fact]
    public void A_name_of_exactly_the_limit_passes_through_unchanged()
    {
        var name = new string('a', StackNames.MaxLength);
        Assert.Equal(name, StackNames.ToPulumi(name));
    }

    [Fact]
    public void A_longer_name_is_shortened_to_the_limit_deterministically()
    {
        var name = "shop.dev." + new string('x', 120) + ".database";

        var mapped = StackNames.ToPulumi(name);

        Assert.Equal(StackNames.MaxLength, mapped.Length);
        Assert.StartsWith(name[..83], mapped);
        Assert.Equal(mapped, StackNames.ToPulumi(name));
    }

    [Fact]
    public void Long_names_that_differ_only_in_the_cut_off_part_map_to_different_names()
    {
        var common = "shop.dev." + new string('x', 120);

        Assert.NotEqual(StackNames.ToPulumi(common + ".database"), StackNames.ToPulumi(common + ".queue"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    [InlineData("a b")]
    public void A_name_Pulumi_or_the_file_system_cannot_take_is_rejected(string name) =>
        Assert.Throws<ArgumentException>(() => StackNames.ToPulumi(name));
}
