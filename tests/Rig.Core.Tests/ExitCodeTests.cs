namespace Rig.Core.Tests;

public sealed class ExitCodeTests
{
    [Fact]
    public void An_empty_plan_is_clean() => Assert.Equal(ExitCode.Clean, ExitCodes.ForPlan(0));

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    public void A_plan_with_changes_is_drift(int changes) => Assert.Equal(ExitCode.Drift, ExitCodes.ForPlan(changes));

    [Fact]
    public void A_negative_change_count_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ExitCodes.ForPlan(-1));

    [Fact]
    public void Exit_codes_match_the_target_architecture()
    {
        Assert.Equal(0, (int)ExitCode.Clean);
        Assert.Equal(1, (int)ExitCode.Drift);
        Assert.Equal(2, (int)ExitCode.Error);
    }
}
