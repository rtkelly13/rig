using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;

namespace Rig.Core.Tests.Desktop.Windows;

/// <summary>
/// Window inventory over a fake Win32 layer: which windows count as the user's, how they sort, what is matched by
/// title or process, and the monitor reads. Replaces the read tests of <c>desktop_manager.win32.inventory</c> and
/// <c>desktop_manager.win32.geometry</c>.
/// </summary>
public sealed class WindowInventoryTests
{
    private static readonly Rect LeftScreen = new(0, 0, 1920, 1080);
    private static readonly Rect LeftWork = new(0, 0, 1920, 1040);
    private static readonly Rect RightScreen = new(1920, 0, 2560, 1440);
    private static readonly Rect RightWork = new(1920, 0, 2560, 1400);

    private readonly FakeWin32Windows win32 = new();

    private WindowInventory Inventory => new(win32, win32);

    [Fact]
    public void Lists_only_the_users_windows_on_the_current_desktop()
    {
        TwoScreens();
        win32.Processes[10] = @"C:\Apps\Code.exe";
        win32.Processes[11] = @"C:\Windows\SearchHost.exe";
        win32.Processes[12] = @"C:\Apps\tool.exe";
        win32.Add(new(1, "  Editor  ", 10) { Rect = LeftWork });
        win32.Add(new(2, "Hidden", 10) { Visible = false });
        win32.Add(new(3, "   ", 10));
        win32.Add(new(4, "Program Manager", 10));
        win32.Add(new(5, "Palette", 12) { Tool = true });
        win32.Add(new(6, "No owner", 0));
        win32.Add(new(7, "Mine", 999));
        win32.Add(new(8, "Search", 11));
        win32.Add(new(9, "Elsewhere", 10) { Desktop = 2 });
        win32.Add(new(13, "Unnamed process", 12345));
        win32.Add(new(14, null, 10));

        var windows = Inventory.CurrentDesktopWindows();

        Assert.Equal(["Unnamed process", "Editor"], windows.Select(window => window.Title));
        var editor = windows[1];
        Assert.Equal(("Code.exe", @"C:\Apps\Code.exe", "Window", (int?)1), (editor.ProcessName, editor.ExePath, editor.ClassName, editor.ScreenNumber));
        Assert.Equal(LeftWork, editor.Bounds);
        Assert.Equal(1, editor.InferredPlacement?.Screen);
        Assert.Null(windows[0].ProcessName);
        Assert.Null(windows[0].Bounds);
        Assert.Null(windows[0].ScreenNumber);
    }

    [Fact]
    public void Sorts_current_desktop_windows_by_process_then_lowercase_title()
    {
        win32.Processes[1] = "b.exe";
        win32.Processes[2] = "a.exe";
        win32.Add(new(1, "zeta", 1));
        win32.Add(new(2, "Beta", 2));
        win32.Add(new(3, "alpha", 2));

        Assert.Equal(["alpha", "Beta", "zeta"], Inventory.CurrentDesktopWindows().Select(window => window.Title));
    }

    [Fact]
    public void A_window_off_every_inferred_placement_still_gets_a_screen_number()
    {
        TwoScreens();
        win32.Processes[1] = "a.exe";
        win32.Add(new(1, "Small", 1) { Rect = new(2000, 100, 50, 40) });
        win32.Add(new(2, "Offscreen", 1) { Rect = new(-5000, -5000, 50, 40) });

        var windows = Inventory.CurrentDesktopWindows();

        Assert.Equal([null, 2], windows.Select(window => window.ScreenNumber));
        Assert.All(windows, window => Assert.Null(window.InferredPlacement));
    }

    [Fact]
    public void Handles_on_the_current_desktop_keep_z_order_and_skip_preserved()
    {
        win32.Processes[1] = "a.exe";
        win32.Add(new(30, "z", 1));
        win32.Add(new(20, "a", 1));
        win32.Add(new(10, "m", 1) { Desktop = 2 });
        win32.Add(new(40, "kept", 1));

        Assert.Equal([30L, 20, 40], Inventory.CurrentDesktopWindowHandles());
        Assert.Equal([30L, 20], Inventory.CurrentDesktopWindowHandles(new HashSet<long> { 40 }));
    }

    [Fact]
    public void Reusable_windows_span_desktops_and_sort_by_lowercase_process_title_then_handle()
    {
        TwoScreens();
        win32.Processes[1] = "B.exe";
        win32.Processes[2] = "a.exe";
        win32.Add(new(5, "Same", 2) { Desktop = 3, Rect = RightWork });
        win32.Add(new(4, "Same", 2) { Desktop = 2 });
        win32.Add(new(3, "x", 1));
        win32.Add(new(2, "skip", 1));

        var windows = Inventory.ReusableWindows(new HashSet<long> { 2 });

        Assert.Equal([4L, 5, 3], windows.Select(window => window.Handle));
        Assert.Equal([2, 3, 1], windows.Select(window => window.DesktopNumber));
        Assert.Equal(2, windows[1].ScreenNumber);
        Assert.Equal(2, windows[1].InferredPlacement?.Screen);
        Assert.Equal(4, Inventory.ReusableWindows().Count);
    }

    [Fact]
    public void Reusable_windows_off_every_placement_fall_back_to_the_screen_holding_their_centre()
    {
        TwoScreens();
        win32.Processes[1] = "a.exe";
        win32.Add(new(1, "Small", 1) { Rect = new(100, 100, 40, 40) });

        var window = Assert.Single(Inventory.ReusableWindows());

        Assert.Equal(1, window.ScreenNumber);
        Assert.Null(window.InferredPlacement);
    }

    [Theory]
    [InlineData("editor", true, new long[] { 1 })]
    [InlineData("edit", false, new long[] { 1, 2 })]
    [InlineData("strasse", false, new long[] { 3 })]
    [InlineData("nothing", false, new long[0])]
    public void Matches_windows_by_casefolded_title(string target, bool exact, long[] expected)
    {
        win32.Add(new(1, " Editor ", 1));
        win32.Add(new(2, "Editing notes", 1));
        win32.Add(new(3, "STRASSE", 1));
        win32.Add(new(4, "Editor hidden", 1) { Visible = false });
        win32.Add(new(5, "editor", 1) { Desktop = 2 });
        win32.Add(new(6, null, 1));

        Assert.Equal(expected, Inventory.MatchingWindows(target, exact));
    }

    [Fact]
    public void Title_matching_can_span_every_desktop()
    {
        win32.Add(new(5, "editor", 1) { Desktop = 2 });

        Assert.Empty(Inventory.MatchingWindows("editor"));
        Assert.Equal([5L], Inventory.MatchingWindows("editor", exact: true, currentDesktopOnly: false));
    }

    [Fact]
    public void Matches_windows_by_casefolded_process_name()
    {
        win32.Processes[1] = @"C:\Apps\Code.exe";
        win32.Processes[2] = @"C:\Apps\other.exe";
        win32.Add(new(10, "one", 1));
        win32.Add(new(11, " two ", 1) { Desktop = 2 });
        win32.Add(new(12, "three", 2));
        win32.Add(new(13, "hidden", 1) { Visible = false });
        win32.Add(new(14, "orphan", 0));
        win32.Add(new(15, null, 1));
        win32.Add(new(16, "unnamed", 3));

        Assert.Equal([10L, 15], Inventory.WindowsMatchingProcess("CODE.EXE"));
        Assert.Equal([10L, 11, 15], Inventory.WindowsMatchingProcess("code.exe", currentDesktopOnly: false));
        Assert.Equal(new HashSet<int> { 1 }, Inventory.ProcessIdsForProcessName("Code.exe"));
        Assert.Equal(new Dictionary<long, string> { [10] = "one", [11] = " two " }, Inventory.WindowTitlesForProcess("Code.exe"));
    }

    [Fact]
    public void Process_names_are_read_for_every_process_windowed_or_not()
    {
        win32.Processes[1] = @"C:\Apps\Code.exe";
        win32.Processes[2] = "/usr/bin/Code.exe";
        win32.Processes[3] = "";
        win32.Processes[4] = null;

        Assert.Equal(new HashSet<string> { "code.exe" }, Inventory.RunningProcessNames());
        Assert.Equal(new HashSet<int> { 1, 2 }, Inventory.ProcessIdsForImageName("CODE.exe"));
        Assert.True(Inventory.IsProcessRunning("Code.EXE"));
        Assert.False(Inventory.IsProcessRunning("missing.exe"));
        Assert.Equal(new ProcessDetails(), Inventory.ProcessDetailsOf(3));
        Assert.Equal(new ProcessDetails("Code.exe", @"C:\Apps\Code.exe"), Inventory.ProcessDetailsOf(1));
    }

    [Fact]
    public void Numbers_work_areas_left_to_right_then_top_to_bottom()
    {
        win32.AddMonitor(new(0, 1080, 1920, 1080), new(0, 1080, 1920, 1040));
        TwoScreens();

        Assert.Equal([LeftWork, new(0, 1080, 1920, 1040), RightWork], Inventory.WorkAreas());
        Assert.Equal([1, 2, 3], Inventory.DisplayAreas().Select(area => area.ScreenNumber));
    }

    [Theory]
    [InlineData("left", 0)]
    [InlineData("center", 1)]
    [InlineData("right", 1)]
    public void Finds_a_work_area_by_slot(string slot, int index)
    {
        TwoScreens();

        Assert.Equal(new[] { LeftWork, RightWork }[index], Inventory.WorkAreaBySlot(slot));
    }

    [Fact]
    public void Slot_and_number_lookups_reject_what_is_not_there()
    {
        Assert.Throws<DesktopException>(() => Inventory.WorkAreaBySlot("left"));
        Assert.Throws<DesktopException>(() => Inventory.PrimaryWorkArea());
        TwoScreens();
        Assert.Throws<ArgumentException>(() => Inventory.WorkAreaBySlot("top"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Inventory.WorkAreaByNumber(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Inventory.WorkAreaByNumber(3));
        Assert.Equal(RightWork, Inventory.WorkAreaByNumber(2));
        Assert.Equal(LeftWork, Inventory.PrimaryWorkArea());
    }

    [Fact]
    public void Reads_one_window()
    {
        TwoScreens();
        win32.Add(new(1, "  Title ", 1) { Rect = RightWork });
        win32.Add(new(2, null, 1) { Minimized = true, Maximized = true });
        win32.Add(new(3, "x", 1) { Maximized = true });

        Assert.Equal("Title", Inventory.Title(1));
        Assert.Null(Inventory.Title(2));
        Assert.Equal(RightWork, Inventory.Bounds(1));
        Assert.True(Inventory.IsWindow(1));
        Assert.False(Inventory.IsWindow(99));
        Assert.Equal(2, Inventory.ScreenNumberOf(1));
        Assert.Null(Inventory.ScreenNumberOf(2));
        Assert.Equal(2, Inventory.InferredPlacementOf(1)?.Screen);
        Assert.Null(Inventory.InferredPlacementOf(2));
        Assert.Equal([ShowState.Normal, ShowState.Minimized, ShowState.Maximized], new long[] { 1, 2, 3 }.Select(Inventory.ShowStateOf));
    }

    [Fact]
    public void Grows_a_target_by_the_windows_invisible_borders()
    {
        win32.Add(new(1, "framed", 1) { Rect = new(93, 0, 1934, 1047), Frame = new(100, 0, 1920, 1040) });
        win32.Add(new(2, "no frame", 1) { Rect = new(0, 0, 10, 10) });
        win32.Add(new(3, "no rect", 1) { Frame = new(0, 0, 10, 10) });
        var target = new Rect(0, 0, 960, 1040);

        Assert.Equal(new Rect(-7, 0, 974, 1047), Inventory.AdjustForFrame(1, target));
        Assert.Equal(target, Inventory.AdjustForFrame(2, target));
        Assert.Equal(target, Inventory.AdjustForFrame(3, target));
    }

    [Fact]
    public void Preserves_the_top_level_foreground_and_console_windows()
    {
        win32.Add(new(1, "dialog", 1) { RootOwner = 10, Root = 11 });
        win32.Add(new(2, "child", 1) { Root = 20 });
        win32.Add(new(3, "top", 1));

        Assert.Empty(Inventory.HandlesToPreserve());
        Assert.Null(Inventory.ForegroundWindow());

        win32.Foreground = 1;
        win32.Console = 2;
        Assert.Equal(new HashSet<long> { 10, 20 }, Inventory.HandlesToPreserve());

        win32.Foreground = 3;
        Assert.Equal(3, Inventory.ForegroundWindow());
        Assert.Equal(20, Inventory.ConsoleWindow());
    }

    private void TwoScreens()
    {
        // Enumerated right first, so numbering has to sort them.
        win32.AddMonitor(RightScreen, RightWork);
        win32.AddMonitor(LeftScreen, LeftWork, primary: true);
    }
}
