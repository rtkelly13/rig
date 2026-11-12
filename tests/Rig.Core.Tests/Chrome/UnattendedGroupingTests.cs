using System.Diagnostics;
using Rig.Core.Chrome;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Chrome;

/// <summary>
/// Waiting for an idle machine and retrying grouping that the user's input stopped. Replaces the unattended half of
/// <c>test_chrome_organise</c>.
/// </summary>
public sealed class UnattendedGroupingTests
{
    private readonly FakePresence _presence = new FakePresence().Idle(600);
    private readonly FakeChromeGroupActions _actions = new();
    private readonly List<string> _said = [];
    private readonly List<string> _reads = [];
    private int _notes;

    private ChromeWindow[] Windows { get; set; } = [];

    private UnattendedGrouping Grouping() =>
        new(new GroupRunner(_actions, new NoChromeDesktops()), _presence, _said.Add, Sleep, () => _presence.Now / 1000.0);

    private void Sleep(double seconds) => _presence.Now += (uint)(seconds * 1000);

    private static ChromeTab Tab(string title, int id) => new(new RuntimeId([id]), 1, id, title, title, false, false, true, false);

    private static GroupPlan Plan(int moves) =>
        new([.. Enumerable.Range(0, moves).Select(index => new GroupMove(Tab($"t{index}", index), 1, "Ryan", "k", "rig", index == 0, 1, 1))], [], 0, 0, [], []);

    private IReadOnlyList<ChromeWindow> Read(string phase)
    {
        _reads.Add(phase);
        return Windows;
    }

    private GroupingResult Run(GroupPlan plan, Func<string, GroupPlan> replan, bool unattended = true) =>
        Grouping().GroupUntilLeftAlone(plan, Read, replan, unattended, () => _notes++);

    // The user touches the machine during the first `times` group steps.
    private void InputDuring(int times)
    {
        var seen = 0;
        _actions.DuringGroup = () =>
        {
            if (seen++ < times)
            {
                _presence.LastInput = _presence.Now + 1000;
                _presence.Now += 1000;
            }
        };
    }

    [Fact]
    public void A_clear_machine_groups_at_once()
    {
        Assert.Equal(new GroupingWait(null, "clear_at_once"), Grouping().Await(() => [], 0.5, 5));
        Assert.Empty(_said);
    }

    [Fact]
    public void A_blocked_machine_with_no_wait_defers()
    {
        _presence.Locked = true;
        Assert.Equal(
            new GroupingWait("the session is locked", "no_wait", LastBlock: "the session is locked"),
            Grouping().Await(() => [], 0.5, 0));
    }

    [Fact]
    public void A_busy_machine_is_waited_for_until_it_has_been_left_alone()
    {
        _presence.Idle(10);

        var wait = Grouping().Await(() => [], 0.5, 5);

        Assert.Null(wait.Deferred);
        Assert.Equal("clear", wait.Outcome);
        Assert.Equal(20, wait.WaitedSeconds, 3);
        Assert.Equal(5, wait.Checks);
        Assert.Equal("the machine was used 25s ago (waits for 0.5 min idle)", wait.LastBlock);
        Assert.Equal(["Waiting up to 5 min to group: the machine was used 10s ago (waits for 0.5 min idle)", "Clear to group after 20s (4 checks)"], _said);
    }

    [Fact]
    public void A_machine_that_stays_locked_gives_up_after_the_budget()
    {
        _presence.Locked = true;

        var wait = Grouping().Await(() => [], 0.5, 1);

        Assert.Equal("gave_up", wait.Outcome);
        Assert.StartsWith("still not clear after 1 min (", wait.Deferred, StringComparison.Ordinal);
        Assert.EndsWith("checks): the session is locked", wait.Deferred, StringComparison.Ordinal);
        Assert.Equal(60, wait.WaitedSeconds, 3);
    }

    [Fact]
    public void The_wait_is_traced_with_a_check_event_per_look()
    {
        List<Activity> stopped = [];
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Rig.Chrome",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        _presence.Locked = true;
        Grouping().Await(() => [], 0.5, 0);
        _presence.Locked = false;
        _presence.Idle(10);

        Grouping().Await(() => [], 0.5, 5);

        Assert.Equal("the session is locked", stopped[0].GetTagItem("wait.last_block"));
        var wait = stopped[1];
        Assert.Equal("clear", wait.GetTagItem("wait.outcome"));
        Assert.Equal(5, wait.GetTagItem("wait.checks"));
        Assert.Equal([true, true, true, true, false], wait.Events.Select(check => (bool)check.Tags.First(tag => tag.Key == "blocked").Value!));
        Assert.Equal(20.0, wait.Events.First().Tags.First(tag => tag.Key == "hint_s").Value);
    }

    [Fact]
    public void A_call_that_ends_before_the_next_look_clears_with_the_first_reason()
    {
        var looks = 0;
        ChromeWindow[] Live() => looks++ == 0
            ? [new ChromeWindow(1, "w", "Default", "Ryan", 1, false, 1, [Tab("Meeting", 1) with { Alerts = ["Microphone recording"] }])]
            : [];

        var wait = Grouping().Await(Live, 0.5, 5);

        Assert.Equal(new GroupingWait(null, "clear", 0, 2, "a call is live: microphone recording (Meeting)"), wait);
    }

    [Fact]
    public void An_attended_run_is_not_stopped_by_input()
    {
        InputDuring(1);

        var result = Run(Plan(3), _ => throw new InvalidOperationException("no replan"), unattended: false);

        Assert.Equal(new GroupingResult(3, 3, true), result);
        Assert.Equal(0, _notes);
    }

    [Fact]
    public void Input_pauses_grouping_with_a_note_and_it_goes_again_once_left_alone()
    {
        InputDuring(1);

        var result = Run(Plan(3), _ => Plan(3));

        Assert.Equal(new GroupingResult(3, 4, true, Retries: 1), result);
        Assert.Equal(1, _notes);
        Assert.Equal(["wait_check"], _reads.Distinct());
        Assert.StartsWith("Paused grouping: keyboard or mouse input; goes again after 30s idle (waits up to 5 min)", _said[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Input_that_keeps_coming_leaves_grouping_to_the_next_run()
    {
        InputDuring(int.MaxValue);
        var replans = 0;

        var result = Run(Plan(2), _ =>
        {
            replans++;
            return Plan(2);
        });

        Assert.Equal(UnattendedGrouping.InputRetries, _notes);
        Assert.Equal(UnattendedGrouping.InputRetries, replans);
        Assert.Equal(new GroupingResult(2, 3, true, "keyboard or mouse input again after 2 retries", true, 2), result);
        Assert.Equal("interrupted", result.Outcome(null));
    }

    [Fact]
    public void Grouping_that_fails_by_itself_does_not_retry()
    {
        _actions.GroupFailure = "menu never opened";

        var result = Run(Plan(2), _ => throw new InvalidOperationException("no replan"));

        Assert.Equal(0, _notes);
        Assert.Equal(new GroupingResult(2, 0, false, "at rig tab 1 in window 1: menu never opened"), result);
        Assert.Equal("stopped", result.Outcome(null));
    }

    [Fact]
    public void A_retry_whose_wait_never_clears_leaves_the_rest()
    {
        InputDuring(1);
        _actions.DuringGroup += () => _presence.Locked = true;

        var result = Run(Plan(2), _ => throw new InvalidOperationException("no replan"));

        Assert.StartsWith("keyboard or mouse input, then still not clear after 5 min", result.Stopped, StringComparison.Ordinal);
        Assert.True(result.ByInput);
    }

    [Fact]
    public void A_retry_with_nothing_left_to_group_finishes()
    {
        InputDuring(1);

        var result = Run(Plan(2), phase => phase == "retry" ? Plan(0) : throw new InvalidOperationException(phase));

        Assert.Equal(new GroupingResult(2, 1, true, null, false, 1), result);
    }

    [Theory]
    [InlineData("menu", 0, false, null, "deferred")]
    [InlineData(null, 0, false, null, "nothing_to_group")]
    [InlineData(null, 2, false, null, "grouped")]
    [InlineData(null, 2, true, "input", "interrupted")]
    [InlineData(null, 2, false, "failed", "stopped")]
    public void Outcomes_name_how_grouping_went(string? deferred, int planned, bool byInput, string? stopped, string outcome) =>
        Assert.Equal(outcome, new GroupingResult(planned, Stopped: stopped, ByInput: byInput).Outcome(deferred));

    [Fact]
    public void A_live_call_skips_the_run_and_says_why()
    {
        using var run = new Activity("run");
        Windows = [new ChromeWindow(1, "w", "Default", "Ryan", 1, false, 1, [Tab("Meeting", 1) with { Alerts = ["Microphone recording"] }])];

        Assert.True(Grouping().SkipIfLive(Windows, run));
        Assert.Equal(["Skipped: a call is live: microphone recording (Meeting); left everything for the next run"], _said);
        Assert.Equal("skipped_live", run.GetTagItem("run.outcome"));
        Assert.False(Grouping().SkipIfLive([]));
    }

    [Fact]
    public void The_paused_note_says_how_long_to_leave_the_machine()
    {
        Assert.Contains("Leave the machine alone for 30 seconds and it carries on by itself;", UnattendedGrouping.PausedNoteLines);
        Assert.Contains("it waits up to 5 minutes, then leaves the rest to the next hourly run.", UnattendedGrouping.PausedNoteLines);
    }
}
