using System.Globalization;

namespace Rig.Cli.Tests;

public sealed class ScheduledLaunchTests
{
    [Theory]
    [InlineData(new[] { "--detach-console", "jobs", "run" }, new[] { "jobs", "run" }, true)]
    [InlineData(new[] { "jobs", "--detach-console" }, new[] { "jobs", "--detach-console" }, false)]
    [InlineData(new string[0], new string[0], false)]
    public void Only_a_leading_detach_flag_is_taken(string[] args, string[] rest, bool detach)
    {
        var split = ScheduledLaunch.Split(args);
        Assert.Equal(rest, split.Args);
        Assert.Equal(detach, split.Detach);
    }

    [Fact]
    public void The_parent_pid_is_taken_out_of_the_environment_so_children_do_not_inherit_it()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { [ScheduledLaunch.ParentVariable] = "16040" };

        var pid = ScheduledLaunch.TakeParent(name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value);

        Assert.Equal("16040", pid);
        Assert.Null(environment[ScheduledLaunch.ParentVariable]);
    }

    [Fact]
    public void Without_a_parent_pid_the_environment_is_left_alone()
    {
        var writes = 0;
        Assert.Null(ScheduledLaunch.TakeParent(_ => null, (_, _) => writes++));
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a pid")]
    public void No_parent_is_watched_without_a_pid(string? pid)
    {
        var gone = 0;
        Assert.False(ScheduledLaunch.WatchParent(pid, () => gone++));
        Assert.Equal(0, gone);
    }

    [Fact]
    public void A_live_parent_is_watched_and_left_running()
    {
        var gone = 0;
        Assert.True(ScheduledLaunch.WatchParent(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), () => gone++));
        Assert.Equal(0, gone);
    }

    [Fact]
    public void A_parent_that_has_already_gone_ends_the_run_at_once()
    {
        var gone = 0;
        Assert.True(ScheduledLaunch.WatchParent(int.MaxValue.ToString(CultureInfo.InvariantCulture), () => gone++));
        Assert.Equal(1, gone);
    }
}
