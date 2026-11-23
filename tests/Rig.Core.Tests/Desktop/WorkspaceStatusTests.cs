using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;
using Rig.Core.Tests.Desktop.Windows;

namespace Rig.Core.Tests.Desktop;

/// <summary>Status captures and draft exports from the live window inventory.</summary>
public sealed class WorkspaceStatusTests
{
    private static FakeWin32Windows Machine()
    {
        var win32 = new FakeWin32Windows { CurrentProcessId = 1 };
        win32.AddMonitor(new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), primary: true);
        win32.Processes[10] = @"C:\Apps\notes.exe";
        win32.Processes[11] = @"C:\Windows\System32\WindowsTerminal.exe";
        win32.Add(new(1, "Notes", 10) { Rect = new(0, 0, 960, 1040), Desktop = 1 });
        win32.Add(new(2, "Terminal", 11) { Rect = new(960, 0, 960, 1040), Desktop = 1 });
        win32.Add(new(3, "Mail", 10) { Rect = new(0, 0, 1920, 1040), Desktop = 2 });
        return win32;
    }

    [Fact]
    public void A_status_names_each_desktop_and_groups_its_windows()
    {
        var win32 = Machine();
        var inventory = new WindowInventory(win32, win32);

        var status = WorkspaceStatus.Current(inventory.ReusableWindows(), 2);

        Assert.Equal(["Desktop 1", "Desktop 2"], status.Desktops.Select(desktop => desktop.Name));
        Assert.Equal(["Notes", "Terminal"], status.Desktops[0].CapturedWindows.Select(window => window.Title));
        Assert.Equal(["Mail"], status.Desktops[1].CapturedWindows.Select(window => window.Title));
    }

    [Fact]
    public void A_status_for_desktop_indexes_follows_the_order_asked_for()
    {
        var win32 = Machine();
        var inventory = new WindowInventory(win32, win32);

        var status = WorkspaceStatus.CurrentForDesktopIndexes(inventory.ReusableWindows(), [2, 1]);

        Assert.Equal(["Desktop 2", "Desktop 1"], status.Desktops.Select(desktop => desktop.Name));
        Assert.Equal("Mail", status.Desktops[0].CapturedWindows[0].Title);
    }

    [Fact]
    public void No_desktop_indexes_is_an_empty_status()
    {
        var win32 = Machine();

        Assert.Empty(WorkspaceStatus.CurrentForDesktopIndexes(new WindowInventory(win32, win32).ReusableWindows(), []).Desktops);
    }

    [Fact]
    public void A_desktop_1_terminal_becomes_a_workspace_terminal_when_a_workspace_is_given()
    {
        var win32 = Machine();
        var inventory = new WindowInventory(win32, win32);

        var captured = WorkspaceStatus.Capture(inventory.ReusableWindows(), 2, "/code");

        var work = captured.Desktops[0];
        Assert.Equal("Desktop 1", work.Name);
        Assert.Equal(2, work.Launch.Count);
        var terminal = Assert.Single(work.Launch, entry => entry.Type == LaunchEntry.Terminal);
        Assert.Equal("Workspace Terminal 1", terminal.Name);
        Assert.Equal("{workspace}", terminal.Cwd);
        Assert.Equal("opencode", terminal.Command);
        Assert.Contains(work.Launch, entry => entry.Type == "app" && entry.Path == @"C:\Apps\notes.exe");
    }

    [Fact]
    public void A_capture_without_a_workspace_leaves_terminals_as_apps()
    {
        var win32 = Machine();
        var inventory = new WindowInventory(win32, win32);

        var captured = WorkspaceStatus.Capture(inventory.ReusableWindows(), 1);

        Assert.All(captured.Desktops[0].Launch, entry => Assert.Equal("app", entry.Type));
    }

    [Fact]
    public void The_first_three_terminal_slots_subdivide()
    {
        var windows = new[]
        {
            new WindowSnapshot("One", ProcessName: "wt.exe", ExePath: @"C:\wt.exe"),
            new WindowSnapshot("Two", ProcessName: "wt.exe", ExePath: @"C:\wt.exe"),
            new WindowSnapshot("Three", ProcessName: "wt.exe", ExePath: @"C:\wt.exe"),
        };

        var entries = WorkspaceStatus.LaunchEntriesFor(windows, 1, "/code");

        Assert.Equal(new PlacementSpec(2, new("thirds", 2), new("halves", 1, "horizontal")), entries[1].Placement);
        Assert.Equal(new PlacementSpec(2, new("thirds", 3), new("halves", 2, "horizontal")), entries[2].Placement);
    }

    [Fact]
    public void A_current_desktop_capture_names_the_desktop()
    {
        var win32 = Machine();
        var inventory = new WindowInventory(win32, win32);

        var captured = WorkspaceStatus.CurrentDesktop(inventory.CurrentDesktopWindows(), "Work");

        Assert.Equal("Work", captured.Name);
        Assert.NotEmpty(captured.CapturedWindows);
    }
}
