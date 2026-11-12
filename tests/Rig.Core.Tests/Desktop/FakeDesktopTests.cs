using Rig.Core.Desktop;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Desktop;

/// <summary>The fake machine behaves the way restore's tests rely on, so a restore test failure is restore's.</summary>
public sealed class FakeDesktopTests
{
    private static readonly Dictionary<string, string> NoSubs = [];
    private static readonly PlacementSpec Left = new(1, new("halves", 1, "vertical"));

    private static WindowWait Wait(int desktop, int seconds = 5) => new(TimeSpan.FromSeconds(seconds), desktop);

    [Fact]
    public void A_launched_window_shows_after_its_startup_time()
    {
        var desktop = new FakeDesktop { Startup = TimeSpan.FromSeconds(2) };
        desktop.EnsureDesktopCount(2, TimeSpan.Zero);
        var entry = new LaunchEntry("Notes") { Path = @"C:\Apps\Notes.exe", Placement = Left };

        Assert.Equal(5001, desktop.Launch(entry, NoSubs));
        Assert.True(desktop.IsProcessRunning("notes.exe"));
        Assert.Empty(desktop.InventoryReusableWindows(new HashSet<long>()));

        var match = desktop.WaitForWindowForEntry(entry, Wait(2), RunLog.Discard);

        Assert.Equal(TimeSpan.FromSeconds(5), desktop.Now);
        var window = desktop.Window(Assert.IsType<long>(match.Handle));
        Assert.NotNull(window);
        Assert.True(match.MovedToTargetDesktop);
        Assert.Equal(2, window.Desktop);
        Assert.Equal(["Notes"], desktop.Launches);

        var placed = desktop.ApplyWindowPlacement(entry, Wait(2), RunLog.Discard, match.Handle);
        Assert.Equal(new PlacementOutcome(true), placed);
        Assert.True(desktop.IsWindowTargetConfirmed(window.Handle, 2, entry));
        Assert.True(desktop.IsEntryTargetConfirmed(entry, 2, TimeSpan.Zero));
    }

    [Fact]
    public void A_hung_launch_runs_but_never_shows()
    {
        var desktop = new FakeDesktop();
        var entry = new LaunchEntry("Hung") { Path = @"C:\hung.exe" };
        desktop.HungLaunches.Add("Hung");

        desktop.Launch(entry, NoSubs);

        Assert.True(desktop.IsProcessRunning("hung.exe"));
        Assert.Equal(new WindowMatch(null), desktop.WaitForWindowForEntry(entry, Wait(1), RunLog.Discard));
        Assert.Equal(new PlacementOutcome(false), desktop.ApplyWindowPlacement(entry, Wait(1), RunLog.Discard));
        Assert.Empty(desktop.WindowsMatchingProcess("hung.exe"));
    }

    [Fact]
    public void A_windowless_launch_starts_nothing_until_its_count_runs_out()
    {
        var desktop = new FakeDesktop();
        var entry = new LaunchEntry("Flaky") { WindowProcessName = "flaky.exe", WindowTitle = "Flaky Window" };
        desktop.WindowlessLaunches["Flaky"] = 1;

        desktop.Launch(entry, NoSubs);
        Assert.False(desktop.IsProcessRunning("flaky.exe"));

        desktop.Launch(entry, NoSubs);
        Assert.Equal(["Flaky Window"], desktop.WindowTitlesForProcess("FLAKY.EXE").Values);
        Assert.Equal(["Flaky", "Flaky"], desktop.Launches);
    }

    [Fact]
    public void Retiring_a_desktop_moves_its_windows_and_renumbers_the_rest()
    {
        var desktop = new FakeDesktop();
        desktop.EnsureDesktopCount(4, TimeSpan.Zero);
        desktop.RenameDesktop(2, "Old");
        desktop.RenameDesktop(4, "Spare");
        desktop.Current = 4;
        var onOld = desktop.AddWindow("a", "a.exe", 2);
        var onThird = desktop.AddWindow("b", "b.exe", 3);

        var removed = desktop.RetireSwitchedOffDesktops(1, ["old", "SPARE", "missing"]);

        Assert.Equal(["Old", "Spare"], removed);
        Assert.Equal(["Desktop 1", "Desktop 3"], desktop.DesktopNames);
        Assert.Equal(1, onOld.Desktop);
        Assert.Equal(2, onThird.Desktop);
        Assert.Equal(1, desktop.Current);
    }

    [Fact]
    public void The_last_desktop_is_never_retired()
    {
        var desktop = new FakeDesktop();

        Assert.Empty(desktop.RetireSwitchedOffDesktops(1, ["Desktop 1"]));
        Assert.Equal(["Desktop 1"], desktop.DesktopNames);
    }

    [Fact]
    public void A_vanishing_window_fails_the_move_and_is_gone()
    {
        var desktop = new FakeDesktop();
        var window = desktop.AddWindow("Notes", "notes.exe");
        desktop.Vanishing.Add(window.Handle);

        Assert.Throws<DesktopException>(() => desktop.MoveExistingWindowToEntry(window.Live(), 1, new("Notes")));
        Assert.Null(desktop.Window(window.Handle));
    }

    [Fact]
    public void Moving_a_window_reports_what_changed()
    {
        var desktop = new FakeDesktop();
        desktop.EnsureDesktopCount(2, TimeSpan.Zero);
        var window = desktop.AddWindow("Notes", "notes.exe", placement: Left);

        Assert.Equal(new WindowMove(false, false), desktop.MoveExistingWindowToEntry(window.Live(), 1, new("Notes") { Placement = Left }));
        Assert.Equal(new WindowMove(true, false), desktop.MoveExistingWindowToEntry(window.Live(), 2, new("Notes")));
        Assert.Equal(Left, window.Placement);
    }

    [Fact]
    public void Closing_a_desktop_keeps_the_preserved_and_other_desktops_windows()
    {
        var desktop = new FakeDesktop();
        desktop.EnsureDesktopCount(2, TimeSpan.Zero);
        var invoking = desktop.AddWindow("shell", "pwsh.exe");
        desktop.InvokingHandles.Add(invoking.Handle);
        desktop.AddWindow("a", "a.exe");
        var other = desktop.AddWindow("b", "b.exe", 2);

        Assert.Equal(1, desktop.CloseCurrentDesktopWindows(desktop.WindowHandlesToPreserve()));
        Assert.Equal([invoking, other], desktop.Windows);
    }

    [Fact]
    public void Switching_desktops_stays_within_the_ones_there_are()
    {
        var desktop = new FakeDesktop();
        desktop.EnsureDesktopCount(3, TimeSpan.Zero);

        desktop.MoveRight(5);
        Assert.Equal(3, desktop.CurrentDesktopNumber());
        desktop.SwitchLeftmost(5);
        Assert.Equal(1, desktop.CurrentDesktopNumber());
        Assert.False(desktop.EnsureCurrentDesktop(4, TimeSpan.Zero));
        Assert.True(desktop.EnsureCurrentDesktop(2, TimeSpan.Zero));
        Assert.Equal(2, desktop.Current);
    }

    [Fact]
    public void Status_captures_the_visible_windows_on_each_desktop()
    {
        var desktop = new FakeDesktop();
        desktop.EnsureDesktopCount(2, TimeSpan.Zero);
        desktop.AddWindow("Notes", "notes.exe", 2, Left);

        var status = desktop.CurrentWorkspaceStatus();

        Assert.Equal(["Desktop 1", "Desktop 2"], status.Desktops.Select(plan => plan.Name));
        Assert.Equal("Notes", Assert.Single(status.Desktops[1].CapturedWindows).Title);
        Assert.Equal("Here", desktop.CurrentDesktopStatus("Here").Name);
        Assert.Equal("Desktop 3", Assert.Single(desktop.CurrentWorkspaceStatus([3]).Desktops).Name);
    }

    [Fact]
    public void A_second_pass_re_places_apps_but_not_terminals()
    {
        var desktop = new FakeDesktop();
        var app = desktop.AddWindow("Notes", "notes.exe");
        var shell = desktop.AddWindow("T", "WindowsTerminal.exe");
        IReadOnlyList<LaunchEntry> entries =
        [
            new("Notes") { Path = @"C:\notes.exe", Placement = Left },
            new("T") { Type = LaunchEntry.Terminal, WindowProcessName = "WindowsTerminal.exe", Placement = Left },
        ];

        var actions = desktop.ApplySecondPassPlacements(new(), new Dictionary<int, IReadOnlyList<LaunchEntry>> { [1] = entries }, RunLog.Discard);

        Assert.Equal(["Desktop 1: second-pass placement"], actions);
        Assert.Equal(Left, app.Placement);
        Assert.Null(shell.Placement);
        Assert.Equal(1, desktop.BringProcessWindowsToFront(new HashSet<string> { "NOTES.EXE" }));
    }

    [Fact]
    public void Sleeping_moves_only_the_fake_clock()
    {
        var desktop = new FakeDesktop();

        desktop.Sleep(TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.FromMinutes(10), desktop.Now);
    }
}
