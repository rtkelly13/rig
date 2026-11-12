using Rig.Core.Desktop;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Desktop;

/// <summary>The pieces restore is built from: its step timer, plan rendering, sibling reservations and run log.</summary>
public sealed class RestorePartsTests
{
    private static readonly PlacementSpec Left = new(1, new("halves", 1, "vertical"));

    [Fact]
    public void A_slow_step_is_logged_and_every_step_summarised()
    {
        var now = TimeSpan.Zero;
        var log = new FakeRunLog();
        var timer = new StepTimer(log, "test", () => now);

        using (timer.Step("restore.plan"))
        {
            now += TimeSpan.FromSeconds(20);
        }

        var step = timer.Step("restore.desktop", label: "desktop 1 (Work)", slowAfter: StepTimer.SlowDesktop);
        now += TimeSpan.FromSeconds(1.25);
        step.Set("ignored", 1);
        step.Dispose();
        step.Dispose();
        using (timer.Step("other"))
        {
        }

        timer.LogSummary();

        Assert.Equal(["plan 20.0s (slow)", "desktop 1 (Work) 1.2s", "other 0.0s"], timer.Taken);
        Assert.Equal(
            ["Slow step: plan took 20.0s (over 15s)", "Step timings: plan 20.0s (slow), desktop 1 (Work) 1.2s, other 0.0s"],
            log.Lines);
    }

    [Fact]
    public void A_timer_with_no_steps_logs_no_summary()
    {
        var log = new FakeRunLog();

        new StepTimer(log, "test", () => TimeSpan.Zero).LogSummary();

        Assert.Empty(log.Lines);
    }

    [Theory]
    [InlineData("My Shell!", "my-shell")]
    [InlineData("!!!", "terminal")]
    [InlineData("Ärger", "rger")]
    public void A_terminal_restore_token_is_a_slug_and_eight_hex_digits(string name, string slug)
    {
        var token = StepTimer.NewTerminalRestoreToken(new(name));

        Assert.Matches($"^{slug}-[0-9a-f]{{8}}$", token);
        Assert.NotEqual(token, StepTimer.NewTerminalRestoreToken(new(name)));
    }

    [Fact]
    public void Wait_timeouts_take_the_entry_own_or_the_default()
    {
        var plain = new LaunchEntry("App");
        var own = plain with { WindowWaitTimeoutMs = 2500 };
        var zero = plain with { WindowWaitTimeoutMs = 0 };
        var terminal = plain with { Type = LaunchEntry.Terminal, Shell = LaunchEntry.DefaultTerminalShell };

        Assert.Equal(TimeSpan.FromSeconds(10), StepTimer.EntryWindowWaitTimeout(plain));
        Assert.Equal(TimeSpan.FromSeconds(10), StepTimer.EntryWindowWaitTimeout(zero));
        Assert.Equal(TimeSpan.FromSeconds(2.5), StepTimer.EntryWindowWaitTimeout(own));
        Assert.Equal(TimeSpan.FromSeconds(15), StepTimer.EntryTitleTakeoverTimeout(plain));
        Assert.Equal(TimeSpan.FromSeconds(2.5), StepTimer.EntryTitleTakeoverTimeout(own));
        Assert.Equal(TimeSpan.FromSeconds(40), StepTimer.EntryWindowWaitGrace(plain));
        Assert.Equal(TimeSpan.Zero, StepTimer.EntryWindowWaitGrace(terminal));
        Assert.True(StepTimer.ShouldForceTerminalRelaunch(terminal));
        Assert.False(StepTimer.ShouldForceTerminalRelaunch(terminal with { Shell = "pwsh" }));
        Assert.False(StepTimer.ShouldForceTerminalRelaunch(plain));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("work", 1)]
    [InlineData("ADMIN", 2)]
    public void A_target_desktop_resolves_to_its_position_ignoring_case(string? target, int? index)
    {
        var plan = new WorkspacePlan { Desktops = [new("Work"), new("Admin")] };

        Assert.Equal(index, RestoreExecutionPlan.ResolveTargetDesktopIndex(plan, target));
    }

    [Fact]
    public void An_unknown_target_desktop_names_the_ones_there_are()
    {
        var plan = new WorkspacePlan { Desktops = [new("Work"), new("Admin")] };

        var failure = Assert.Throws<ArgumentException>(() => RestoreExecutionPlan.ResolveTargetDesktopIndex(plan, "Play"));

        Assert.Equal("Desktop 'Play' not found in plan. Available: Work, Admin", failure.Message);
    }

    [Fact]
    public void Actions_render_each_kind_of_step()
    {
        var entry = new LaunchEntry("Editor") { Path = @"C:\Apps\Code.exe" };
        var desktop = new DesktopPlan("Work") { Launch = [entry] };
        var planned = new PlannedDesktopExecution(desktop, 1, closeExisting: true)
        {
            EntrySteps =
            [
                new(desktop, 1, entry, "app `Editor` -> C:\\Apps\\Code.exe", EntryAction.Launch),
                new(desktop, 1, entry, "ignored", EntryAction.Satisfied),
                new(desktop, 1, entry, "ignored", EntryAction.Reuse, new LiveWindow(1, "Editor", 10, "Code.exe")),
                new(desktop, 1, entry, "reuse without a window", EntryAction.Reuse),
            ],
            OrphanWindows = [new LiveWindow(2, "Bare", null, null), new LiveWindow(3, "Chat", 11, "Slack.exe", DesktopNumber: 1, ScreenNumber: 2)],
        };

        var actions = new RestoreExecutionPlan { Desktops = [planned] }.RenderActions();

        Assert.Equal(
            [
                "Desktop 1: Work",
                "  - close existing windows on this desktop",
                "  - app `Editor` -> C:\\Apps\\Code.exe",
                "  - already in place `Editor`",
                "  - reuse window `Editor`",
                "  - reuse without a window",
                "  - orphan open window `Bare`",
                "  - orphan open window `Chat [Slack.exe]` on desktop 1 screen 2",
            ],
            actions);
    }

    [Fact]
    public void A_sibling_titled_window_is_reserved_against_adoption()
    {
        var machine = new FakeDesktop();
        var docs = machine.AddWindow("Docs - Visual Studio Code", "Code.exe");
        machine.AddWindow("Editor - Visual Studio Code", "Code.exe");
        var editor = new LaunchEntry("Editor") { WindowProcessName = "Code.exe", WindowTitle = "Editor", Placement = Left };
        var desktop = new DesktopPlan("Work")
        {
            Launch =
            [
                editor,
                editor with { Name = "Docs", WindowTitle = "docs" },
                editor with { Name = "Off", WindowTitle = "Off", Disabled = true },
                editor with { Name = "Mail", WindowProcessName = "Outlook.exe", WindowTitle = "Inbox" },
            ],
        };
        var log = new FakeRunLog();
        var run = new RestoreRun(new() { Desktops = [desktop] }, machine, log, new Dictionary<string, string>(), new HashSet<long>(), new());
        var planned = new PlannedDesktopExecution(desktop, 1, closeExisting: false);

        Assert.Equal([docs.Handle], RestoreSteps.SiblingOwnedHandles(run, planned, editor));
        Assert.Equal($"Reserved sibling-owned window(s) [{docs.Handle}] against adoption by Editor", Assert.Single(log.Lines));
        Assert.Empty(RestoreSteps.SiblingOwnedHandles(run, planned, editor with { WindowProcessName = null }));
        Assert.Empty(RestoreSteps.SiblingOwnedHandles(run, planned, desktop.Launch[3]));
    }

    [Fact]
    public void A_run_log_appends_to_its_own_file_and_rewrites_the_latest()
    {
        using var temp = new TempDir();
        var folder = Path.Combine(temp.Path, "logs");
        var log = new FileRunLog(folder, new FixedClock(new(2026, 11, 12, 9, 30, 5, TimeSpan.Zero)));

        log.Log("first");
        log.LogFailure("FAILED Editor: boom", new DesktopException("boom"));

        Assert.Equal(Path.Combine(folder, "workspace-restore-20261112-093005.log"), log.RunPath);
        Assert.Equal(Path.Combine(folder, "workspace-restore-latest.log"), log.LatestPath);
        var expected = "[2026-11-12T09:30:05+00:00] first\n[2026-11-12T09:30:05+00:00] FAILED Editor: boom\n"
            + "Rig.Core.Desktop.DesktopException: boom\n";
        Assert.Equal(expected, File.ReadAllText(log.RunPath));
        Assert.Equal(expected, File.ReadAllText(log.LatestPath));
        Assert.Equal(2, log.Lines.Count);
    }

    [Fact]
    public void A_new_run_replaces_the_latest_log_but_keeps_the_earlier_run()
    {
        using var temp = new TempDir();
        var earlier = new FileRunLog(temp.Path, new FixedClock(new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)));
        earlier.Log("earlier");
        var later = new FileRunLog(temp.Path, new FixedClock(new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero)));

        later.Log("later");

        Assert.Equal("[2026-11-12T09:00:00+00:00] earlier\n", File.ReadAllText(earlier.RunPath));
        Assert.Equal("[2026-11-12T10:00:00+00:00] later\n", File.ReadAllText(later.LatestPath));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
