using Xunit;

namespace jd.cli.tests;

// The one pass over a command's arguments that every scan makes.
public sealed class ArgumentsTests
{
    [Fact]
    public void A_value_option_given_twice_is_the_last_one()
    {
        var (scanned, _) = Arguments.Scan(["preview", "w.yaml", "--env", "a.yaml", "--env", "b.yaml"], 1);
        var (_, values, _) = Arguments.ScanLabel(["1.0.0", "--to", "a.yaml", "--to", "b.yaml"], "--to");

        Assert.Equal("b.yaml", scanned?.Environment);
        Assert.Equal("b.yaml", values["--to"]);
    }

    [Fact]
    public void A_value_option_takes_the_next_argument_even_when_it_reads_as_an_option()
    {
        var (scanned, problem) = Arguments.Scan(["--env", "--json", "w.yaml"], 0);

        Assert.Empty(problem);
        Assert.Equal(("--json", false), (scanned?.Environment, scanned?.Json ?? true));
        Assert.Equal(["w.yaml"], scanned?.Positional ?? []);
    }
}
