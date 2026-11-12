using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;

namespace Rig.Core.Tests.Desktop.Windows;

/// <summary>
/// Window writes over fake Win32 layers: the calls each action makes, in order, and which refusals fail it. Replaces
/// the write tests of <c>desktop_manager.win32.geometry</c> and <c>send_input</c>.
/// </summary>
public sealed class WindowActionsTests
{
    private readonly FakeWin32Windows reads = new();
    private readonly FakeWin32Actions writes = new();

    private WindowActions Actions => new(writes, new WindowInventory(reads, reads));

    [Fact]
    public void Move_restores_then_moves_the_visible_frame_then_bobs_through_topmost()
    {
        reads.Add(new(1, "framed", 1) { Rect = new(93, 0, 1934, 1047), Frame = new(100, 0, 1920, 1040) });

        Actions.Move(1, new(0, 0, 960, 1040));

        Assert.Equal(["Restore 1", "MoveWindow 1 -7,0,974,1047", "Restack 1 Topmost", "Restack 1 NotTopmost"], writes.Calls);
    }

    [Theory]
    [InlineData("MoveWindow")]
    [InlineData("Restack")]
    public void Move_fails_when_windows_refuses(string refused)
    {
        writes.Refuse.Add(refused);

        var error = Assert.Throws<DesktopException>(() => Actions.Move(1, new(0, 0, 10, 10)));

        Assert.Contains("window 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Place_repositions_without_restoring_or_restacking()
    {
        Actions.Place(4, new(10, 20, 30, 40));

        Assert.Equal(["Reposition 4 10,20,30,40"], writes.Calls);
        writes.Refuse.Add("Reposition");
        Assert.Throws<DesktopException>(() => Actions.Place(4, new(10, 20, 30, 40)));
    }

    [Fact]
    public void Bringing_to_front_shows_without_activating_and_ignores_refusals()
    {
        writes.Refuse.Add("ShowWithoutActivating");
        writes.Refuse.Add("Restack");

        Actions.BringToFront(2);

        Assert.Equal(["ShowWithoutActivating 2", "Restack 2 Top"], writes.Calls);
    }

    [Fact]
    public void Closes_the_users_windows_on_the_current_desktop_apart_from_preserved()
    {
        reads.Processes[1] = "a.exe";
        reads.Add(new(1, "one", 1));
        reads.Add(new(2, "two", 1));
        reads.Add(new(3, "elsewhere", 1) { Desktop = 2 });
        reads.Add(new(4, "kept", 1));

        Assert.Equal(2, Actions.CloseCurrentDesktopWindows(new HashSet<long> { 4 }));
        Assert.Equal(["PostClose 1", "PostClose 2"], writes.Calls);
    }

    [Fact]
    public void Focus_reports_whether_windows_allowed_it()
    {
        Assert.True(Actions.Focus(1));
        writes.Refuse.Add("SetForeground");
        Assert.False(Actions.Focus(1));
    }

    [Fact]
    public void Places_a_placement_on_the_monitor_it_names()
    {
        reads.AddMonitor(new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), primary: true);
        var placement = new PlacementSpec(1, new PlacementLayout("halves", Slot: 1, Orientation: "vertical"));

        Assert.Equal(new Rect(0, 0, 960, 1040), Actions.PlacementRectFor(placement));
        Assert.Throws<ArgumentOutOfRangeException>(() => Actions.PlacementRectFor(placement with { Screen = 2 }));
    }

    [Fact]
    public void Sending_keys_succeeds_only_when_windows_takes_every_event()
    {
        var keys = KeyEvent.Tap(0x0D);

        Assert.True(Actions.SendKeys([]));
        Assert.True(Actions.SendKeys(keys));
        writes.KeysTaken = 1;
        Assert.False(Actions.SendKeys(keys));
        Assert.Equal(["SendKeys 2", "SendKeys 2"], writes.Calls);
    }

    [Fact]
    public void Text_types_a_down_and_an_up_per_utf16_unit()
    {
        var events = KeyEvent.Text("a😀");

        Assert.Equal(6, events.Count);
        Assert.Equal(new KeyEvent(0, 'a', KeyEvent.Unicode), events[0]);
        Assert.Equal(new KeyEvent(0, 'a', KeyEvent.Unicode | KeyEvent.KeyUp), events[1]);
        Assert.Equal(new KeyEvent(0, 0xD83D, KeyEvent.Unicode), events[2]);
        Assert.Equal(new KeyEvent(0, 0xDE00, KeyEvent.Unicode | KeyEvent.KeyUp), events[5]);
        Assert.Equal([new KeyEvent(0x1B, 0, 0), new KeyEvent(0x1B, 0, KeyEvent.KeyUp)], KeyEvent.Tap(0x1B));
    }

    [Fact]
    public void Terminates_and_waits_for_processes()
    {
        Assert.True(Actions.TerminateProcess(7));
        Assert.True(Actions.WaitForProcessExit(7));
        Assert.Equal(["Terminate 7 1", "WaitForExit 7 00:00:05"], writes.Calls);

        writes.Calls.Clear();
        writes.Exited = false;
        Assert.False(Actions.WaitForProcessExit(7, TimeSpan.FromSeconds(-3)));
        writes.Exited = null;
        Assert.True(Actions.WaitForProcessExit(7, TimeSpan.FromSeconds(1)));
        Assert.Equal(["WaitForExit 7 00:00:00", "WaitForExit 7 00:00:01"], writes.Calls);
    }

    private sealed class FakeWin32Actions : IWin32Actions
    {
        public List<string> Calls { get; } = [];

        public HashSet<string> Refuse { get; } = [];

        public int? KeysTaken { get; set; }

        public bool? Exited { get; set; } = true;

        public bool Restore(long handle) => Record("Restore", $"{handle}");

        public bool ShowWithoutActivating(long handle) => Record("ShowWithoutActivating", $"{handle}");

        public bool MoveWindow(long handle, Rect rect) => Record("MoveWindow", $"{handle} {Edges(rect)}");

        public bool Reposition(long handle, Rect rect) => Record("Reposition", $"{handle} {Edges(rect)}");

        public bool Restack(long handle, ZOrder order) => Record("Restack", $"{handle} {order}");

        public bool PostClose(long handle) => Record("PostClose", $"{handle}");

        public bool SetForeground(long handle) => Record("SetForeground", $"{handle}");

        public int SendKeys(IReadOnlyList<KeyEvent> events)
        {
            _ = Record("SendKeys", $"{events.Count}");
            return KeysTaken ?? events.Count;
        }

        public bool Terminate(int pid, int exitCode) => Record("Terminate", $"{pid} {exitCode}");

        public bool? WaitForExit(int pid, TimeSpan timeout)
        {
            _ = Record("WaitForExit", $"{pid} {timeout}");
            return Exited;
        }

        private static string Edges(Rect rect) => $"{rect.Left},{rect.Top},{rect.Width},{rect.Height}";

        private bool Record(string call, string args)
        {
            Calls.Add($"{call} {args}");
            return !Refuse.Contains(call);
        }
    }
}
