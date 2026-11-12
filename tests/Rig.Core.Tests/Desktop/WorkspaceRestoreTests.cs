using System.Diagnostics;
using Rig.Core.Desktop;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// What restore does to a desktop, run against <see cref="FakeDesktop"/> instead of Windows. Each scenario sets a
/// machine up, runs the real restore against it, and asserts on where the windows ended up and what was launched.
/// Ported from <c>tests/desktop_manager/test_restore_behaviour.py</c>.
/// </summary>
public sealed class WorkspaceRestoreTests
{
    private static readonly PlacementSpec Left = new(1, new("halves", 1, "vertical"));
    private static readonly PlacementSpec Right = new(1, new("halves", 2, "vertical"));
    private static readonly PlacementSpec Full2 = new(2, new("full"));

    private static LaunchEntry App(string name, string exe, PlacementSpec? placement = null) =>
        new(name) { Type = "app", Path = $@"C:\Apps\{exe}", WindowProcessName = exe, WindowTitle = name, Placement = placement ?? Left, DelayMs = 0 };

    private static LaunchEntry Unplaced(string name, string exe) => App(name, exe) with { Placement = null };

    private static LaunchEntry Terminal(string name, string title) =>
        new(name)
        {
            Type = LaunchEntry.Terminal,
            Shell = LaunchEntry.DefaultTerminalShell,
            Command = "claude",
            Cwd = @"C:\code",
            WindowProcessName = "WindowsTerminal.exe",
            WindowTitle = title,
            Placement = Left,
            DelayMs = 0,
        };

    private static DesktopPlan Desktop(string name, params LaunchEntry[] entries) => new(name) { Launch = entries };

    private static WorkspacePlan Plan(params DesktopPlan[] desktops) => new() { Desktops = desktops, SwitchDelayMs = 0 };

    private static IReadOnlyList<string> Restore(
        WorkspacePlan plan,
        FakeDesktop machine,
        bool dryRun = false,
        bool closeExisting = false,
        bool diffExisting = false,
        string? target = null,
        IRunLog? log = null) =>
        WorkspaceRestore.Run(
            plan,
            new(new Dictionary<string, string>()) { DryRun = dryRun, CloseExisting = closeExisting, DiffExisting = diffExisting, TargetDesktop = target },
            machine,
            log ?? RunLog.Discard);

    private static void Names(FakeDesktop machine, params string[] names)
    {
        machine.DesktopNames.Clear();
        machine.DesktopNames.AddRange(names);
    }

    [Fact]
    public void A_dry_run_plans_reuse_launch_and_orphans_without_touching_the_machine()
    {
        var machine = new FakeDesktop();
        machine.AddWindow("Editor", "Code.exe", placement: Right);
        machine.AddWindow("Unrelated chat", "Slack.exe", placement: Full2);
        var before = machine.Windows.Select(window => (window.Handle, window.Desktop, window.Placement)).ToList();

        var actions = Restore(Plan(Desktop("Work", App("Editor", "Code.exe"), App("Notes", "Obsidian.exe", Right))), machine, dryRun: true, diffExisting: true);

        Assert.Equal("Desktop 1: Work", actions[0]);
        Assert.Equal("  - reuse window `Editor` from desktop 1", actions[1]);
        Assert.Equal(@"  - app `Notes` -> C:\Apps\Obsidian.exe", actions[2]);
        Assert.Equal("  - orphan open window `Unrelated chat [Slack.exe]` on desktop 1 screen 2", actions[3]);
        Assert.Empty(machine.Launches);
        Assert.Equal(before, machine.Windows.Select(window => (window.Handle, window.Desktop, window.Placement)));
        Assert.Equal(["Desktop 1"], machine.DesktopNames);
    }

    [Fact]
    public void A_dry_run_with_close_reports_the_close()
    {
        var actions = Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), new FakeDesktop(), dryRun: true, closeExisting: true);

        Assert.Equal(["Desktop 1: Work", "  - close existing windows on this desktop", @"  - app `Editor` -> C:\Apps\Code.exe"], actions);
    }

    [Fact]
    public void A_dry_run_lists_the_desktops_a_switched_off_feature_retires()
    {
        var actions = Restore(Plan(Desktop("Work")) with { RetiredDesktopNames = ["Personal"] }, new FakeDesktop(), dryRun: true);

        Assert.Equal(["Remove switched-off desktop(s), if present: Personal", "Desktop 1: Work"], actions);
    }

    [Fact]
    public void A_dry_run_for_one_desktop_lists_only_that_one()
    {
        var actions = Restore(Plan(Desktop("Work"), Desktop("Admin", App("Mail", "Outlook.exe"))), new FakeDesktop(), dryRun: true, target: "ADMIN");

        Assert.Equal(["Desktop 2: Admin", @"  - app `Mail` -> C:\Apps\Outlook.exe"], actions);
    }

    [Fact]
    public void An_unknown_target_desktop_is_reported_and_nothing_runs()
    {
        var machine = new FakeDesktop();

        var actions = Restore(Plan(Desktop("Work"), Desktop("Admin")), machine, target: "Play");

        Assert.Equal(["Desktop 'Play' not found in plan. Available: Work, Admin"], actions);
        Assert.Equal(["Desktop 1"], machine.DesktopNames);
    }

    [Fact]
    public void An_empty_plan_does_nothing()
    {
        Assert.Equal(["No desktops configured."], Restore(Plan(), new FakeDesktop()));
    }

    [Fact]
    public void A_missing_entry_launches_into_its_planned_slot()
    {
        var machine = new FakeDesktop { Startup = TimeSpan.FromSeconds(2) };
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), machine, diffExisting: true, log: log);

        Assert.Equal(["Editor"], machine.Launches);
        var editor = Assert.Single(machine.Windows);
        Assert.Equal((1, Left), (editor.Desktop, editor.Placement));
        Assert.Contains("Launched Editor pid=5001", log.Lines);
        Assert.Contains("First-pass placement verified for Editor", log.Lines);
        Assert.Contains("Desktop 1 completion check passed on attempt 1", log.Lines);
        Assert.Contains("First pass fully successful; skipped second-pass placement", log.Lines);
        Assert.Equal("Restore finished successfully", log.Lines[^1]);
    }

    [Fact]
    public void An_open_window_is_reused_instead_of_launching_another()
    {
        var machine = new FakeDesktop();
        var open = machine.AddWindow("Editor", "Code.exe", placement: Right);
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), machine, diffExisting: true, log: log);

        Assert.Empty(machine.Launches);
        Assert.Equal([open], machine.Windows);
        Assert.Equal(Left, open.Placement);
        Assert.Contains(log.Lines, line => line.StartsWith($"Repositioned reused window {open.Handle} for Editor to {{", StringComparison.Ordinal));
    }

    [Fact]
    public void An_entry_already_in_its_slot_is_left_where_it_is()
    {
        var machine = new FakeDesktop();
        machine.AddWindow("Editor", "Code.exe", placement: Left);

        var actions = Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), machine, diffExisting: true);

        Assert.Empty(machine.Launches);
        Assert.Single(machine.Windows);
        Assert.Contains("  - reuse window `Editor` from desktop 1", actions);
    }

    [Fact]
    public void A_vanished_reuse_relaunches()
    {
        var machine = new FakeDesktop();
        var doomed = machine.AddWindow("Editor", "Code.exe", placement: Right);
        machine.Vanishing.Add(doomed.Handle);
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), machine, diffExisting: true, log: log);

        Assert.Equal(["Editor"], machine.Launches);
        var editor = Assert.Single(machine.Windows);
        Assert.NotEqual(doomed.Handle, editor.Handle);
        Assert.Equal((1, Left), (editor.Desktop, editor.Placement));
        var failure = Assert.Single(log.Failures);
        Assert.Equal($"FAILED reuse Editor: window {doomed.Handle} vanished before it could be moved; falling back to relaunch", failure.Message);
    }

    [Fact]
    public void A_failed_reuse_is_not_retried_against_the_same_window()
    {
        var machine = new FakeDesktop();
        var doomed = machine.AddWindow("Editor", "Code.exe", placement: Full2);
        machine.Vanishing.Add(doomed.Handle);

        var actions = Restore(Plan(Desktop("Work", App("Editor", "Code.exe", Left), App("Docs", "Code.exe", Right))), machine, diffExisting: true);

        Assert.Equal(["  - reuse window `Editor` from desktop 1"], actions.Where(action => action.Contains("reuse window", StringComparison.Ordinal)));
        Assert.Equal(["Editor", "Docs"], machine.Launches);
        Assert.Equal(
            [("Docs", Right), ("Editor", Left)],
            machine.Windows.Where(window => window.Placement is not null).Select(window => (window.Title, window.Placement!)).OrderBy(pair => pair.Title, StringComparer.Ordinal));
    }

    [Fact]
    public void A_desktop_whose_entry_did_not_appear_is_retried()
    {
        var machine = new FakeDesktop();
        machine.WindowlessLaunches["Editor"] = 1;
        using var temp = new TempDir();
        var log = new FileRunLog(temp.Path, new FixedClock(new(2026, 11, 12, 9, 30, 0, TimeSpan.Zero)));

        Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), machine, diffExisting: true, log: log);

        Assert.Equal(["Editor", "Editor"], machine.Launches);
        var editor = Assert.Single(machine.Windows, window => window.VisibleAt <= machine.Now);
        Assert.Equal((1, Left), (editor.Desktop, editor.Placement));
        var text = File.ReadAllText(log.RunPath);
        Assert.Contains("Desktop 1 completion check failed on attempt 1", text, StringComparison.Ordinal);
        Assert.Contains("Retrying desktop 1: attempt 2 of 3", text, StringComparison.Ordinal);
        Assert.Contains("Desktop 1 completion check passed on attempt 2", text, StringComparison.Ordinal);
        Assert.Contains("VERIFY ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retry_waits_for_the_instance_still_starting_and_defers_it_to_the_second_pass()
    {
        // Three attempts against a slow-starting app once spawned three processes (2026-08-05).
        var machine = new FakeDesktop();
        machine.HungLaunches.Add("Editor");
        var log = new FakeRunLog();

        var actions = Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), machine, diffExisting: true, log: log);

        Assert.Equal(["Editor"], machine.Launches);
        Assert.Contains("Not relaunching Editor: the instance launched earlier this run is still starting; waiting for its window instead", log.Lines);
        Assert.Contains("Desktop 1 incomplete after 3 attempt(s); continuing", log.Lines);
        Assert.Contains("Second pass needed for 1 entry(s)", log.Lines);
        Assert.Contains("Desktop 1: second-pass placement", actions);
    }

    [Fact]
    public void A_satisfied_entry_is_left_alone_on_a_retry()
    {
        var machine = new FakeDesktop();
        machine.WindowlessLaunches["Notes"] = 1;
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", App("Editor", "Code.exe"), App("Notes", "Obsidian.exe", Right))), machine, diffExisting: true, log: log);

        Assert.Equal(["Editor", "Notes", "Notes"], machine.Launches);
        Assert.Contains("Leaving already-placed entries alone: ['Editor']", log.Lines);
        Assert.Contains("Skipping Editor: already verified in its planned slot", log.Lines);
    }

    [Fact]
    public void Close_existing_keeps_the_invoking_window()
    {
        var machine = new FakeDesktop();
        var invoking = machine.AddWindow("claude", "WindowsTerminal.exe", placement: Right);
        machine.InvokingHandles.Add(invoking.Handle);
        machine.AddWindow("Stray chat", "Slack.exe", placement: Full2);
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), machine, closeExisting: true, log: log);

        Assert.Equal(["Code.exe", "WindowsTerminal.exe"], machine.Windows.Select(window => window.ProcessName).Order(StringComparer.Ordinal));
        Assert.Equal((1, Right), (invoking.Desktop, invoking.Placement));
        Assert.Contains("Preserving 1 invoking window(s) from reuse/close handling", log.Lines);
        Assert.Contains("Requested close for 1 existing window(s) on Work", log.Lines);
    }

    [Fact]
    public void The_invoking_window_is_never_claimed_for_an_entry_that_looks_like_it()
    {
        var machine = new FakeDesktop();
        var invoking = machine.AddWindow("Shell", "WindowsTerminal.exe", placement: Right);
        machine.InvokingHandles.Add(invoking.Handle);

        var actions = Restore(Plan(Desktop("Work", App("Shell", "WindowsTerminal.exe", Left))), machine, diffExisting: true);

        Assert.DoesNotContain(actions, action => action.Contains("reuse window", StringComparison.Ordinal));
        Assert.Equal(["Shell"], machine.Launches);
        Assert.Equal((1, Right), (invoking.Desktop, invoking.Placement));
    }

    [Fact]
    public void Restore_returns_to_the_desktop_it_started_on()
    {
        var machine = new FakeDesktop { Current = 3 };
        Names(machine, "Work", "Admin", "Spare");
        var log = new FakeRunLog();

        var actions = Restore(Plan(Desktop("Work", App("Editor", "Code.exe")), Desktop("Admin", App("Mail", "Outlook.exe"))), machine, diffExisting: true, log: log);

        Assert.Equal(3, machine.Current);
        Assert.Equal("Return to starting desktop 3", actions[^1]);
        Assert.Equal(new HashSet<(string, int)> { ("Editor", 1), ("Mail", 2) }, machine.Windows.Select(window => (window.Title, window.Desktop)).ToHashSet());
        Assert.Contains("Moved to desktop 2", log.Lines);
        Assert.Contains("Returned to starting desktop 3 (confirmed)", log.Lines);
    }

    [Fact]
    public void Planned_desktops_are_named_and_switched_off_ones_retired()
    {
        var machine = new FakeDesktop();
        Names(machine, "Desktop 1", "Personal", "Desktop 3");
        var stranded = machine.AddWindow("Diary", "Notes.exe", desktop: 2);
        var log = new FakeRunLog();

        var actions = Restore(Plan(Desktop("Work"), Desktop("Admin")) with { RetiredDesktopNames = ["Personal"] }, machine, diffExisting: true, log: log);

        Assert.Equal(["Work", "Admin"], machine.DesktopNames);
        Assert.Equal(2, stranded.Desktop);
        Assert.Equal("Remove switched-off desktop(s), if present: Personal", actions[0]);
        Assert.Contains("Removed switched-off desktop(s): Personal", log.Lines);
    }

    [Fact]
    public void One_target_desktop_is_restored_alone()
    {
        var machine = new FakeDesktop();
        Names(machine, "Work", "Admin");
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", App("Editor", "Code.exe")), Desktop("Admin", App("Mail", "Outlook.exe"))), machine, target: "admin", log: log);

        Assert.Equal(["Mail"], machine.Launches);
        Assert.Equal(2, Assert.Single(machine.Windows).Desktop);
        Assert.Contains("Navigated directly to desktop 2", log.Lines);
    }

    [Fact]
    public void A_launch_failing_on_a_missing_path_opens_its_fallback_note()
    {
        var machine = new FakeDesktop();
        machine.FailingLaunches["Editor"] = @"Launch entry `Editor` resolved path does not exist: C:\Apps\Code.exe";
        var log = new FakeRunLog();

        var actions = Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), machine, log: log);

        Assert.Contains("Open missing-path note for Editor", actions);
        Assert.Contains("Editor", machine.OpenedFallbacks);
        Assert.Contains(log.Failures, failure => failure.Message.StartsWith("FAILED Editor: Launch entry `Editor` resolved path", StringComparison.Ordinal));
    }

    [Fact]
    public void A_launch_failing_otherwise_opens_no_fallback()
    {
        var machine = new FakeDesktop();
        machine.FailingLaunches["Editor"] = "access denied";

        var actions = Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), machine);

        Assert.Empty(machine.OpenedFallbacks);
        Assert.DoesNotContain(actions, action => action.StartsWith("Open missing-path", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_placement_is_logged_and_the_retry_reuses_the_window()
    {
        var machine = new FakeDesktop();
        machine.FailingPlacements.Add("Editor");
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), machine, log: log);

        Assert.Equal(["Editor"], machine.Launches);
        Assert.Contains(log.Failures, failure => failure.Message == "FAILED placement Editor: could not place Editor");
        Assert.Equal(Left, Assert.Single(machine.Windows).Placement);
    }

    [Fact]
    public void An_unplaced_entry_launches_and_is_left_where_it_opens()
    {
        var machine = new FakeDesktop();

        Restore(Plan(Desktop("Work", Unplaced("Mail", "Outlook.exe") with { DelayMs = 500 })), machine);

        Assert.Equal(["Mail"], machine.Launches);
        Assert.Null(Assert.Single(machine.Windows).Placement);
        Assert.True(machine.Now >= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void A_terminal_waits_for_its_window_and_title_before_placement()
    {
        var machine = new FakeDesktop();
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", Terminal("Claude", "Claude Code"))), machine, log: log);

        Assert.Equal(["Claude"], machine.Launches);
        Assert.Contains(log.Lines, line => line.StartsWith("Launched Claude with restore token claude-", StringComparison.Ordinal));
        Assert.Contains("Confirmed UI title takeover for Claude: 'Claude Code'", log.Lines);
        Assert.Equal(Left, Assert.Single(machine.Windows).Placement);
    }

    [Fact]
    public void A_terminal_whose_title_never_changes_fails_readiness()
    {
        var machine = new FakeDesktop();
        machine.StuckTitles.Add("Claude Code");
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", Terminal("Claude", "Claude Code"))), machine, log: log);

        Assert.Contains(log.Failures, failure => failure.Message == "FAILED readiness Claude: Timed out waiting for UI title takeover for Claude");
        Assert.IsType<DesktopException>(log.Failures[0].Failure);
    }

    [Fact]
    public void A_terminal_with_no_window_fails_readiness()
    {
        var machine = new FakeDesktop();
        machine.WindowlessLaunches["Claude"] = 3;
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", Terminal("Claude", "Claude Code"))), machine, log: log);

        Assert.Contains(log.Failures, failure => failure.Message == "FAILED readiness Claude: Could not detect launched window for Claude");
    }

    [Fact]
    public void A_desktop_that_cannot_be_renamed_or_read_still_restores()
    {
        var machine = new FakeDesktop { CurrentUnknown = true };
        machine.Unrenamable.Add(1);
        var log = new FakeRunLog();

        var actions = Restore(Plan(Desktop("Work", Unplaced("Mail", "Outlook.exe"))), machine, log: log);

        Assert.Equal(["Desktop 1"], machine.DesktopNames);
        Assert.Contains("Desktop 1 could not be renamed to 'Work'", log.Lines);
        Assert.Contains("Captured starting desktop: ?", log.Lines);
        Assert.DoesNotContain(actions, action => action.StartsWith("Return to", StringComparison.Ordinal));
    }

    [Fact]
    public void Planned_windows_are_raised_once_the_desktop_is_done()
    {
        var log = new FakeRunLog();

        Restore(Plan(Desktop("Work", App("Editor", "Code.exe"))), new FakeDesktop(), log: log);

        Assert.Contains("Desktop 1 raised 1 planned window(s) above orphan windows", log.Lines);
    }

    [Fact]
    public void Restore_traces_its_run()
    {
        var seen = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StepTimer.Source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (seen)
                {
                    seen.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        Restore(Plan(Desktop("Traced", App("Editor", "Code.exe"))), new FakeDesktop(), diffExisting: true);

        Activity[] mine;
        lock (seen)
        {
            mine = [.. seen];
        }

        Assert.Contains(mine, activity => activity.OperationName == "workspace.restore" && Equals(activity.GetTagItem("scope"), RestoreSteps.Scope) && Equals(activity.GetTagItem("restore.diff_existing"), true));
        Assert.Contains(mine, activity => activity.OperationName == "restore.desktop" && Equals(activity.GetTagItem("desktop.name"), "Traced"));
        Assert.Contains(mine, activity => activity.OperationName == "restore.entry.launch" && Equals(activity.GetTagItem("entry.name"), "Editor"));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
