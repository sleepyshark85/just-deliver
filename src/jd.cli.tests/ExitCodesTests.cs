using jd.orchestrator.usecases;
using Xunit;

namespace jd.cli.tests;

// The usage text's exit codes: 0 success, 1 errors, 2 usage errors.
public sealed class ExitCodesTests
{
    [Theory]
    [InlineData(Outcome.Succeeded, 0)]
    [InlineData(Outcome.Failed, 1)]
    [InlineData(Outcome.InvalidInput, 2)]
    public void Each_outcome_has_the_exit_code_the_usage_states(Outcome outcome, int code) => Assert.Equal(code, ExitCodes.Of(outcome));

    [Fact]
    public void A_value_that_is_no_outcome_is_refused() =>
        Assert.Equal("outcome", Assert.Throws<ArgumentOutOfRangeException>(() => ExitCodes.Of((Outcome)99)).ParamName);
}
