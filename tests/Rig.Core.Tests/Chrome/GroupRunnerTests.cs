using System.Diagnostics;
using Rig.Core.Chrome;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Chrome;

/// <summary>Carrying out a grouping plan: order, desktops, interruptions and failures. Replaces the executor half of <c>test_chrome_organise</c>.</summary>
public sealed class GroupRunnerTests
{
    private const long W1 = 1000;
    private const long W2 = 2000;

    private readonly FakeChromeGroupActions _actions = new();
    private readonly FakeChromeDesktops _desktops = new();
    private readonly List<TimeSpan> _sleeps = [];
    private double _now;
    private int _ids;

    private GroupRunner Runner(Func<bool>? locked = null) =>
        new(_actions, _desktops, locked, pause =>
        {
            _sleeps.Add(pause);
            _now += pause.TotalSeconds;
        }, () => _now);

    private ChromeTab Tab(string title, long window = W1, TabGroup? group = null) =>
        new(new RuntimeId([42, (int)window, _ids++]), window, _ids, title, title, false, false, true, false) { Group = group };

    private static GroupMove Move(ChromeTab tab, string title = "rig", bool isNew = true, long? home = null, int number = 1, int homeNumber = 1) =>
        new(tab, number, "Ryan", "rtkelly13/rig", title, isNew, home ?? tab.WindowHandle, homeNumber);

    private static GroupPlan Plan(IReadOnlyList<GroupMove> moves, IReadOnlyList<Ungroup>? ungroups = null) =>
        new(moves, [], 0, 0, ungroups ?? [], []);

    [Fact]
    public void Ungroups_run_first_and_the_tabs_they_freed_are_moved_as_ungrouped()
    {
        var duplicate = new TabGroup(new RuntimeId([9]), "rig");
        var freed = Tab("a", W2, duplicate);
        var plan = Plan(
            [Move(Tab("b")), Move(freed, isNew: false, home: W1, number: 2)],
            [new Ungroup(duplicate, [freed], 2, "Ryan")]);

        var ran = Runner().Run(plan);

        Assert.Equal(new GroupRun(2), ran);
        Assert.Equal(
            ["front 2000", "ungroup rig", "back 2000", "front 1000", "group b rig new", "back 1000", "front 2000", "group a rig", "back 2000"],
            _actions.Log);
        Assert.Null(_actions.Grouped[1].Group);
    }

    [Fact]
    public void A_new_group_across_windows_carries_the_tab_home_first()
    {
        var plan = Plan([Move(Tab("a", W2), home: W1, number: 2)]);

        Assert.Equal(new GroupRun(1), Runner().Run(plan));
        Assert.Equal(["front 2000", "move a to 1000", "group a rig new", "back 2000"], _actions.Log);
        Assert.Equal(W1, _actions.Grouped[0].WindowHandle);
    }

    [Fact]
    public void A_tab_that_will_not_move_stops_the_run()
    {
        _actions.MoveFailure = "no such window";
        var tab = Tab("a", W2);

        var ran = Runner().Run(Plan([Move(tab, home: W1, number: 2)]));

        Assert.Equal(new GroupRun(0, $"at rig tab {tab.Index + 1} in window 2, moving it to window 1: no such window"), ran);
    }

    [Fact]
    public void The_first_failed_step_stops_the_run_with_the_tabs_moved_so_far()
    {
        _actions.GroupFailure = "menu never opened";
        _actions.FailGroupAt = 1;
        var second = Tab("b");

        var ran = Runner().Run(Plan([Move(Tab("a")), Move(second, isNew: false), Move(Tab("c"), isNew: false)]));

        Assert.Equal(new GroupRun(1, $"at rig tab {second.Index + 1} in window 1: menu never opened"), ran);
        Assert.Equal(2, _actions.Grouped.Count);
    }

    [Fact]
    public void A_failed_ungroup_stops_before_any_move()
    {
        _actions.UngroupFailure = "header gone";
        var duplicate = new TabGroup(new RuntimeId([9]), "rig");
        var tab = Tab("a", W1, duplicate);

        var ran = Runner().Run(Plan([Move(Tab("b"))], [new Ungroup(duplicate, [tab], 1, "Ryan")]));

        Assert.Equal(new GroupRun(0, "ungrouping duplicate rig in window 1: header gone"), ran);
        Assert.Empty(_actions.Grouped);
    }

    [Fact]
    public void Input_stops_the_run_before_the_next_step()
    {
        var asked = 0;
        string? Interrupted() => ++asked == 3 ? "keyboard or mouse input" : null;

        var ran = Runner().Run(Plan([Move(Tab("a")), Move(Tab("b"), isNew: false)]), Interrupted);

        Assert.Equal(new GroupRun(1, "keyboard or mouse input"), ran);
    }

    [Fact]
    public void Input_before_a_window_stops_without_bringing_it_forward()
    {
        var duplicate = new TabGroup(new RuntimeId([9]), "rig");
        var ungroup = new Ungroup(duplicate, [Tab("a", W1, duplicate)], 1, "Ryan");

        Assert.Equal(new GroupRun(0, "stop"), Runner().Run(Plan([], [ungroup]), () => "stop"));
        Assert.Equal(new GroupRun(0, "stop"), Runner().Run(Plan([Move(Tab("b"))]), () => "stop"));
        Assert.Empty(_actions.Log);

        var asked = 0;
        Assert.Equal(new GroupRun(0, "later"), Runner().Run(Plan([], [ungroup]), () => ++asked == 2 ? "later" : null));
        Assert.Equal(["front 1000", "back 1000"], _actions.Log);
    }

    [Fact]
    public void Windows_on_other_desktops_are_brought_here_and_sent_home_after()
    {
        _desktops.Windows[W1] = 1;
        _desktops.Windows[W2] = 3;

        Runner().Run(Plan([Move(Tab("a", W2), number: 2)]));

        Assert.Equal([(W2, 1), (W2, 3)], _desktops.Moves);
        Assert.Empty(_sleeps);
    }

    [Fact]
    public void A_window_that_has_not_arrived_is_waited_for_until_the_deadline()
    {
        _desktops.Windows[W2] = 3;
        _desktops.Stuck.Add(W2);
        _desktops.Gone.Add(W2);

        Runner().Run(Plan([Move(Tab("a", W2), number: 2)]));

        Assert.Equal(GroupRunner.ArrivalSeconds, _now, 3);
        Assert.Equal([(W2, 1)], _desktops.Moves);
    }

    [Fact]
    public void A_window_that_will_not_move_is_left_where_it_is()
    {
        _desktops.Windows[W2] = 3;
        _desktops.Unmovable.Add(W2);

        Assert.Equal(new GroupRun(1), Runner().Run(Plan([Move(Tab("a", W2), number: 2)])));
        Assert.Empty(_desktops.Moves);
    }

    [Fact]
    public void A_failure_while_bringing_windows_sends_the_brought_ones_home()
    {
        _desktops.Windows[W1] = 3;
        _desktops.Windows[W2] = 3;
        _desktops.Stuck.Add(W1);
        var reads = 0;
        var flaky = new FlakyDesktops(_desktops, () => ++reads > 2);

        // The wait reads each window's desktop; a read that throws there ends the run, with the windows sent home.
        Assert.Throws<InvalidOperationException>(() =>
            new GroupRunner(_actions, flaky, sleep: _sleeps.Add, clock: () => _now).Run(Plan([Move(Tab("a")), Move(Tab("b", W2), number: 2)])));
        Assert.Equal([(W1, 1), (W2, 1), (W1, 3), (W2, 3)], _desktops.Moves);
        Assert.Empty(_actions.Log);
    }

    [Fact]
    public void Grouping_that_switched_desktops_switches_back()
    {
        _actions.DuringGroup = () => _desktops.Number = 3;

        Runner().Run(Plan([Move(Tab("a"))]));

        Assert.Equal([1], _desktops.Went);
        Assert.Equal(1, _desktops.Number);
    }

    [Fact]
    public void A_run_that_stays_on_its_desktop_does_not_switch()
    {
        new GroupRunner(_actions, _desktops).Run(Plan([Move(Tab("a"))]));
        Assert.Empty(_desktops.Went);
    }

    [Fact]
    public void Unaddressable_desktops_bring_nothing_and_switch_nothing()
    {
        _desktops.Number = null;
        _desktops.Windows[W2] = 3;

        Assert.Equal(new GroupRun(1), Runner().Run(Plan([Move(Tab("a", W2), number: 2)])));
        Assert.Empty(_desktops.Moves);
        Assert.Empty(_desktops.Went);
    }

    [Fact]
    public void Desktops_that_break_during_the_run_do_not_fail_it()
    {
        _actions.DuringGroup = () => _desktops.Broken = true;

        Assert.Equal(new GroupRun(1), Runner().Run(Plan([Move(Tab("a"))])));
    }

    [Fact]
    public void A_window_that_will_not_come_forward_says_where_it_is()
    {
        _actions.Refuses.Add(W2);
        _desktops.Windows[W2] = 3;
        _desktops.Stuck.Add(W2);

        var ran = Runner().Run(Plan([Move(Tab("a", W2), number: 2)]));

        Assert.Equal(new GroupRun(0, "window 2 would not come to the front (it is on desktop 3, this is desktop 1)"), ran);
    }

    [Fact]
    public void A_window_that_will_not_come_forward_on_a_locked_machine_says_so()
    {
        _actions.Refuses.Add(W1);
        var duplicate = new TabGroup(new RuntimeId([9]), "rig");

        var ran = Runner(() => true).Run(Plan([], [new Ungroup(duplicate, [Tab("a", W1, duplicate)], 1, "Ryan")]));

        Assert.Equal(new GroupRun(0, "window 1 would not come to the front (the session is locked)"), ran);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(null, 1)]
    [InlineData(3, null)]
    public void A_window_here_that_will_not_come_forward_says_only_that(int? home, int? current)
    {
        _actions.Refuses.Add(W1);
        if (home is { } number)
        {
            _desktops.Windows[W1] = number;
        }

        _desktops.Number = current;
        _desktops.Stuck.Add(W1);

        Assert.Equal(new GroupRun(0, "window 1 would not come to the front"), Runner().Run(Plan([Move(Tab("a"))])));
    }

    [Fact]
    public void A_refusal_with_failing_probes_says_only_that_it_refused()
    {
        _actions.Refuses.Add(W1);
        _desktops.UnreadableWindows = true;
        _desktops.Number = null;

        var ran = Runner(() => throw new InvalidOperationException("lock")).Run(Plan([Move(Tab("a"))]));

        Assert.Equal(new GroupRun(0, "window 1 would not come to the front"), ran);
    }

    [Fact]
    public void Planning_looks_up_hosts_only_for_titles_that_name_nothing()
    {
        var keyed = Tab("Fix it · Issue #4 · rtkelly13/rig");
        var bare = Tab("Dashboard");
        var addressed = Tab("Local app") with { Url = new Uri("http://localhost:8094/") };
        var window = new ChromeWindow(W1, "w", "Profile 4", "Ryan", 1, false, 1, [keyed, bare, addressed]);
        var unnamed = new ChromeWindow(W2, "w", null, null, 2, false, 1, [Tab("Other", W2)]) { ProfileScope = string.Empty };
        Dictionary<string, List<string>> asked = [];

        var plan = GroupRunner.Plan(
            [window, unnamed],
            1,
            2,
            false,
            (profile, titles) =>
            {
                asked[profile] = [.. titles];
                return new Dictionary<string, string>(StringComparer.Ordinal);
            },
            UrlOwners.None);

        Assert.Equal(["Profile 4"], asked.Keys);
        Assert.Equal(["Dashboard"], asked["Profile 4"]);
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void A_window_that_will_not_go_home_is_left_here()
    {
        _desktops.Windows[W2] = 3;
        _actions.DuringGroup = () => _desktops.Unmovable.Add(W2);

        Assert.Equal(new GroupRun(1), Runner().Run(Plan([Move(Tab("a", W2), number: 2)])));
        Assert.Equal([(W2, 1)], _desktops.Moves);
    }

    [Fact]
    public void The_run_is_traced_window_by_window()
    {
        List<Activity> stopped = [];
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Rig.Chrome",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var duplicate = new TabGroup(new RuntimeId([9]), "rig");
        var window = new ChromeWindow(W1, "w", "Profile 4", "Ryan", 1, false, 1, [Tab("Dashboard")]);
        GroupRunner.Plan([window], 1, 2, true, (_, _) => new Dictionary<string, string>(StringComparer.Ordinal), UrlOwners.None, "retry");
        _actions.DuringGroup = () => _desktops.Number = 3;
        _actions.GroupFailure = "menu never opened";

        Runner().Run(Plan([Move(Tab("a"))]));
        _actions.UngroupFailure = "header gone";
        Runner().Run(Plan([], [new Ungroup(duplicate, [Tab("b", W1, duplicate)], 1, "Ryan")]));

        Assert.Equal("retry", stopped.Single(activity => activity.OperationName == "plan_grouping").GetTagItem("group.phase"));
        Assert.Equal(["plan_grouping", "group_window", "restore_desktop", "group", "ungroup_window", "group"], stopped.Select(activity => activity.OperationName));
        Assert.Equal("header gone", ((string)stopped[4].GetTagItem("group.stopped")!)[^11..]);
        Assert.EndsWith("menu never opened", (string)stopped[3].GetTagItem("group.stopped")!, StringComparison.Ordinal);
        Assert.Equal(3, stopped[2].GetTagItem("desktop.from"));
    }

    [Fact]
    public void A_lease_restores_once()
    {
        var restored = 0;
        var lease = new ForegroundLease(true, () => restored++);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, restored);
        Assert.True(lease.Arrived);
    }

    private sealed class FlakyDesktops(FakeChromeDesktops inner, Func<bool> fails) : IChromeDesktops
    {
        public int? Current() => inner.Current();

        public int? DesktopOf(long handle) => inner.Moves.Count > 0 && fails() ? throw new InvalidOperationException("read") : inner.DesktopOf(handle);

        public void SwitchTo(int number) => inner.SwitchTo(number);

        public void MoveWindow(long handle, int number) => inner.MoveWindow(handle, number);

        public bool WindowExists(long handle) => inner.WindowExists(handle);
    }
}
