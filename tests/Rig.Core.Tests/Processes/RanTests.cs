using Rig.Core.Processes;

namespace Rig.Core.Tests.Processes;

public sealed class RanTests
{
    [Fact]
    public void A_zero_exit_is_ok_and_exposes_its_output()
    {
        var ran = new Ran(0, "  a\n\n b \r\n", string.Empty);

        Assert.True(ran.Ok);
        Assert.Equal("a\n\n b", ran.OutOrEmpty);
        Assert.Equal(["a", "b"], ran.Lines!);
        Assert.Same(ran, ran.EnsureOk(["git", "status"]));
    }

    [Fact]
    public void A_failure_hides_its_output()
    {
        var ran = new Ran(1, "partial", "boom");

        Assert.False(ran.Ok);
        Assert.Empty(ran.OutOrEmpty);
        Assert.Null(ran.Lines);
    }

    [Theory]
    [InlineData("out", " boom \n", "git status exited 1: boom")]
    [InlineData(" only out ", "", "git status exited 1: only out")]
    [InlineData("", "", "git status exited 1")]
    public void Ensure_ok_throws_with_the_most_useful_detail(string stdout, string stderr, string message)
    {
        var ran = new Ran(1, stdout, stderr);

        var error = Assert.Throws<ProcException>(() => ran.EnsureOk(["git", "status"]));
        Assert.Equal(message, error.Message);
        Assert.Equal(["git", "status"], error.Argv);
        Assert.Same(ran, error.Ran);
    }
}
