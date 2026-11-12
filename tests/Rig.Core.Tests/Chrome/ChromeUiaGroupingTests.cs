using Rig.Core.Chrome;
using Rig.Core.Chrome.Windows;
using Rig.Core.Desktop.Windows;
using Rig.Core.Desktop.Windows.Uia;
using Rig.Core.Tests.Desktop.Windows;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Chrome;

/// <summary>The foreground half of <see cref="ChromeUiaActions"/>: the window is in front and a right-click lands on the tab.</summary>
public sealed class ChromeUiaGroupingTests
{
    private const int ChromePid = 10;
    private const long Window = 1;
    private const long Home = 2;
    private const long Elsewhere = 5;
    private const ushort Escape = 0x1B;
    private readonly FakeWin32Windows _win32 = new();
    private readonly FakeUiAutomation _uia = new();
    private readonly FakeWin32Actions _input = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);
    private readonly FakeUiaElement _root;
    private readonly FakeUiaElement _strip;
    private readonly FakeUiaElement _tab;
    private ChromeUiaReader? _reader;

    public ChromeUiaGroupingTests()
    {
        _win32.Processes[ChromePid] = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        (_root, _strip) = AddWindow(Window, "W - Google Chrome");
        _tab = Tab("Inbox");
        _ = _strip.Add(_tab, Tab("Docs", selected: true));
        _win32.Add(new FakeWindow(Elsewhere, "Editor", 99) { Root = Elsewhere });
        _win32.Foreground = Window;
        _win32.UnderAnyPoint = Window;
        _input.OnSetForeground = handle => _win32.Foreground = handle;
    }

    private ChromeUiaReader Reader => _reader ??= new(_uia, _win32, _win32, new NoShell(), _clock.Advance);

    private ChromeUiaActions Actions => new(Reader, _uia, _win32, _input, _clock, _clock.Advance);

    [Fact]
    public void Foreground_restores_a_minimised_window_and_puts_everything_back_once()
    {
        _win32.Windows[0] = _win32.Windows[0] with { Minimized = true };
        _win32.Foreground = Elsewhere;
        _win32.Cursor = (7, 8);

        var lease = Actions.Foreground(Window);
        var arrived = lease.Arrived;
        var during = _input.Calls.ToList();
        lease.Dispose();
        lease.Dispose();

        Assert.True(arrived);
        Assert.Equal(["SendKeys 2", "Restore 1", "SetForeground 1"], during);
        Assert.Equal(["MoveCursor 7,8", "Minimize 1", "SetForeground 5"], _input.Calls.Skip(during.Count));
        Assert.Equal(Elsewhere, _win32.Foreground);
    }

    [Fact]
    public void Foreground_on_the_lock_screen_does_nothing()
    {
        _win32.Cursor = null;

        using var lease = Actions.Foreground(Window);

        Assert.False(lease.Arrived);
        Assert.Empty(_input.Calls);
    }

    [Fact]
    public void Foreground_asks_twice_and_still_restores_when_refused()
    {
        _win32.Foreground = Elsewhere;
        _input.OnSetForeground = null;

        var lease = Actions.Foreground(Window, TimeSpan.FromMilliseconds(200));
        lease.Dispose();

        Assert.False(lease.Arrived);
        Assert.Equal(2, _input.Calls.Count(call => call == "SetForeground 1"));
        Assert.Equal(2, _input.Keys.Count);
        Assert.Contains("MoveCursor 0,0", _input.Calls);
    }

    [Fact]
    public void Foreground_restores_when_asking_throws()
    {
        _win32.Foreground = Elsewhere;
        _input.OnSetForeground = _ => throw new InvalidOperationException("boom");

        _ = Assert.Throws<InvalidOperationException>(() => Actions.Foreground(Window));

        Assert.Equal("MoveCursor 0,0", _input.Calls[^2]);
    }

    [Fact]
    public void Brings_a_window_forward_only_when_it_lost_the_foreground()
    {
        Assert.True(Actions.BringForward(Window));
        Assert.Empty(_input.Calls);
        Assert.Equal(Window, Actions.ForegroundWindow());

        _win32.Foreground = Elsewhere;
        Assert.True(Actions.BringForward(Window));
        _win32.Foreground = Elsewhere;
        _input.OnSetForeground = null;
        Assert.False(Actions.BringForward(Window, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void Creates_a_new_group_from_the_flat_entry_and_types_its_title()
    {
        var entry = MenuItem("Add tab to new group");
        _input.OnRightClick = _ => AddPopup(50, MenuItem("Add tab to new split view"), entry);
        entry.OnInvoke = () =>
        {
            RemoveWindow(50);
            AddPopup(51, new FakeUiaElement("Textfield TitleField", UiaControlType.Edit) { Focused = true });
        };
        _input.OnKeys = keys =>
        {
            if (keys.SequenceEqual(KeyEvent.Tap(0x0D)))
            {
                RemoveWindow(51);
                MoveIntoGroup(_strip, _tab, "Research");
            }
        };

        var outcome = Actions.GroupTab(Read("Inbox"), "Research", isNew: true);

        Assert.Equal((true, "created"), (outcome.Grouped, outcome.Detail));
        Assert.Equal((50, 15), _input.Cursor);
        Assert.Equal(1, _tab.Scrolls);
        Assert.Equal([KeyEvent.Text("Research"), KeyEvent.Tap(0x0D)], _input.Keys);
    }

    [Fact]
    public void Joins_an_existing_group_through_the_submenu()
    {
        var entry = MenuItem("Add tab to group");
        entry.Expandable = true;
        var research = MenuItem("Research");
        _input.OnRightClick = _ => AddPopup(50, entry);
        entry.OnExpand = () => AddPopup(51, MenuItem("New group"), research);
        research.OnInvoke = () =>
        {
            RemoveWindow(50);
            RemoveWindow(51);
            MoveIntoGroup(_strip, _tab, "Research");
        };

        var outcome = Actions.GroupTab(Read("Inbox"), "Research", isNew: false);

        Assert.Equal((true, "joined"), (outcome.Grouped, outcome.Detail));
        Assert.Empty(_input.Keys);
    }

    [Fact]
    public void Joining_a_group_in_another_window_waits_for_the_tab_to_arrive_there()
    {
        var (_, homeStrip) = AddWindow(Home, "H - Google Chrome");
        var group = MoveIntoGroup(homeStrip, Tab("Inbox"), "Research");
        var entry = MenuItem("Add tab to group");
        entry.Expandable = true;
        var research = MenuItem("Research");
        _input.OnRightClick = _ => AddPopup(50, entry);
        entry.OnExpand = () => AddPopup(51, research);
        research.OnInvoke = () =>
        {
            RemoveWindow(50);
            RemoveWindow(51);
            _strip.Remove(_tab);
            _ = group.Add(Tab("Inbox"));
        };

        var outcome = Actions.GroupTab(Read("Inbox"), "Research", isNew: false, homeHandle: Home);

        Assert.Equal((true, "moved"), (outcome.Grouped, outcome.Detail));
    }

    [Fact]
    public void Grouping_refuses_before_touching_anything()
    {
        var seen = Read("Inbox");
        _uia.Roots.Remove(Home);

        var unreadHome = Actions.GroupTab(seen, "Research", isNew: false, homeHandle: Home);
        _win32.UnderAnyPoint = Elsewhere;
        var covered = Actions.GroupTab(seen, "Research", isNew: true);
        _win32.UnderAnyPoint = 0;
        var nowhere = Actions.GroupTab(seen, "Research", isNew: true);
        _win32.UnderAnyPoint = Window;
        _tab.Offscreen = true;
        var offscreen = Actions.GroupTab(seen, "Research", isNew: true);
        _tab.Offscreen = false;
        _tab.Rect = new UiaRect(10, 10, 10, 40);
        var flat = Actions.GroupTab(seen, "Research", isNew: true);
        _win32.Foreground = Elsewhere;
        _input.OnSetForeground = null;
        var background = Actions.GroupTab(seen, "Research", isNew: true, timeout: TimeSpan.FromMilliseconds(100));
        _tab.NameValue = "Inbox (3)";
        var renamed = Actions.GroupTab(seen, "Research", isNew: true);
        _strip.Remove(_tab);
        var gone = Actions.GroupTab(seen, "Research", isNew: true);

        Assert.Equal("home window could not be read to count its group before the move", unreadHome.Detail);
        Assert.All(new[] { covered, nowhere, offscreen, flat }, outcome => Assert.Equal("tab is not visible where a click would land", outcome.Detail));
        Assert.Equal("window is not in the foreground", background.Detail);
        Assert.Equal("title changed to 'Inbox (3)'", renamed.Detail);
        Assert.Equal("tab no longer present", gone.Detail);
        Assert.DoesNotContain(_input.Calls, call => call.StartsWith("RightClick", StringComparison.Ordinal));
    }

    [Fact]
    public void Grouping_refuses_a_grouped_tab_and_waits_for_a_leftover_menu()
    {
        AddPopup(50, MenuItem("Leftover"));

        var leftover = Actions.GroupTab(Read("Inbox"), "Research", isNew: true, timeout: TimeSpan.FromMilliseconds(100));
        RemoveWindow(50);
        _ = MoveIntoGroup(_strip, _tab, "Research");
        var grouped = Actions.GroupTab(Read("Inbox"), "Other", isNew: true);

        Assert.Equal("a menu from the previous step is still open", leftover.Detail);
        Assert.Equal("tab is already in a group", grouped.Detail);
    }

    [Theory]
    [InlineData("Add tab to new split view", false, false, "context menu did not offer a group entry", 1)]
    [InlineData("Add tab to new group", false, false, "group 'Research' no longer exists", 1)]
    [InlineData("Add tab to group", false, false, "unexpected menu entry 'add tab to group'", 1)]
    [InlineData("Add tab to group", true, false, "group 'Research' not offered", 2)]
    [InlineData("Add tab to group", true, true, "new-group entry not offered", 2)]
    public void A_menu_that_does_not_offer_the_group_is_escaped(string entryName, bool expandable, bool isNew, string detail, int escapes)
    {
        var entry = MenuItem(entryName);
        entry.Expandable = expandable;
        _input.OnRightClick = _ => AddPopup(50, entry);

        var outcome = Actions.GroupTab(Read("Inbox"), "Research", isNew, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Equal((false, detail), (outcome.Grouped, outcome.Detail));
        Assert.Equal(Enumerable.Repeat(KeyEvent.Tap(Escape), escapes), _input.Keys);
    }

    [Fact]
    public void Two_groups_with_the_title_cannot_be_told_apart()
    {
        var entry = MenuItem("Add tab to group");
        entry.Expandable = true;
        _input.OnRightClick = _ => AddPopup(50, entry);
        entry.OnExpand = () => AddPopup(51, MenuItem("Research"), MenuItem(" research "));

        var outcome = Actions.GroupTab(Read("Inbox"), "Research", isNew: false);

        Assert.Equal("2 menu entries named 'Research'; cannot tell them apart", outcome.Detail);
        Assert.Equal(2, _input.Keys.Count);
    }

    [Fact]
    public void A_new_group_whose_title_field_is_missing_or_unfocused_is_left_unnamed()
    {
        var entry = MenuItem("Add tab to new group");
        _input.OnRightClick = _ => AddPopup(50, entry);
        entry.OnInvoke = () => RemoveWindow(50);

        var missing = Actions.GroupTab(Read("Inbox"), "Research", isNew: true, timeout: TimeSpan.FromMilliseconds(100));
        entry.OnInvoke = () =>
        {
            RemoveWindow(50);
            AddPopup(51, new FakeUiaElement("Textfield TitleField", UiaControlType.Edit));
        };
        var unfocused = Actions.GroupTab(Read("Inbox"), "Research", isNew: true, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Equal("group created but its title field did not appear; left unnamed", missing.Detail);
        Assert.Equal("group created but its title field lost focus; left unnamed", unfocused.Detail);
        Assert.Equal([KeyEvent.Tap(Escape)], _input.Keys);
    }

    [Fact]
    public void A_tab_that_never_joins_is_reported()
    {
        var entry = MenuItem("Add tab to new group");
        _input.OnRightClick = _ => AddPopup(50, entry);
        var field = new FakeUiaElement("Textfield TitleField", UiaControlType.Edit) { Focused = true };
        entry.OnInvoke = () =>
        {
            RemoveWindow(50);
            AddPopup(51, field);
        };

        var local = Actions.GroupTab(Read("Inbox"), "Research", isNew: true, timeout: TimeSpan.FromMilliseconds(100));
        RemoveWindow(51);
        _ = AddWindow(Home, "H - Google Chrome");
        var away = Actions.GroupTab(Read("Inbox"), "Research", isNew: true, homeHandle: Home, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Equal("tab did not show up in group 'Research'", local.Detail);
        Assert.Equal("tab did not show up in group 'Research' in its home window", away.Detail);
    }

    [Fact]
    public void Moves_a_tab_to_the_window_its_menu_names()
    {
        var (_, target) = AddWindow(Home, "H - Google Chrome");
        _ = target.Add(Tab("Mail", selected: true), Tab("News"));
        var move = MenuItem("Move tab to another window");
        move.Expandable = true;
        var named = MenuItem("Mail and 1 other tab");
        _input.OnRightClick = _ => AddPopup(50, move);
        move.OnExpand = () => AddPopup(51, MenuItem("New window"), named, MenuItem("Docs"));
        named.OnInvoke = () =>
        {
            RemoveWindow(50);
            RemoveWindow(51);
            _strip.Remove(_tab);
            _ = target.Add(Tab("Inbox"));
        };

        var outcome = Actions.MoveToWindow(Read("Inbox"), Home);

        Assert.Equal((true, "moved"), (outcome.Outcome.Grouped, outcome.Outcome.Detail));
        Assert.Equal((Home, "Inbox"), (outcome.Tab!.WindowHandle, outcome.Tab.Title));
        Assert.NotEqual(Read("Docs").RuntimeId, outcome.Tab.RuntimeId);
    }

    [Fact]
    public void Moving_refuses_before_touching_anything()
    {
        var seen = Read("Inbox");
        var noTarget = Actions.MoveToWindow(seen, Home);
        var (_, target) = AddWindow(Home, "H - Google Chrome");
        _ = target.Add(Tab("Mail"));
        var noActive = Actions.MoveToWindow(seen, Home);
        target.FindAll(UiaScope.Descendants, new UiaCondition(UiaControlType.TabItem)).Cast<FakeUiaElement>().Single().Selected = true;
        _win32.Foreground = Elsewhere;
        _input.OnSetForeground = null;
        var background = Actions.MoveToWindow(seen, Home, TimeSpan.FromMilliseconds(100));
        _win32.Foreground = Window;
        AddPopup(50, MenuItem("Leftover"));
        var leftover = Actions.MoveToWindow(seen, Home, TimeSpan.FromMilliseconds(100));
        RemoveWindow(50);
        _strip.Remove(_tab);
        var gone = Actions.MoveToWindow(seen, Home);

        Assert.Equal("target window has gone or has no active tab", noTarget.Outcome.Detail);
        Assert.Equal("target window has gone or has no active tab", noActive.Outcome.Detail);
        Assert.Equal("window is not in the foreground", background.Outcome.Detail);
        Assert.Equal("a menu from the previous step is still open", leftover.Outcome.Detail);
        Assert.Equal("tab no longer present", gone.Outcome.Detail);
        Assert.Null(gone.Tab);
        Assert.DoesNotContain(KeyEvent.Tap(Escape), _input.Keys);
    }

    [Theory]
    [InlineData(false, false, 0, "context menu did not offer Move tab to another window", 1)]
    [InlineData(true, false, 0, "Move tab to another window did not open a submenu", 1)]
    [InlineData(true, true, 0, "target window not offered", 2)]
    [InlineData(true, true, 2, "2 windows are named like the target; cannot tell them apart", 2)]
    [InlineData(true, true, 1, "tab did not show up in the target window", 0)]
    [InlineData(true, true, -1, "tab is not visible where a click would land", 0)]
    public void A_move_that_cannot_finish_is_reported(bool offered, bool expandable, int named, string detail, int escapes)
    {
        var (_, target) = AddWindow(Home, "H - Google Chrome");
        _ = target.Add(Tab("Mail", selected: true));
        var move = MenuItem(offered ? "Move tab to another window" : "Pin");
        move.Expandable = expandable;
        _input.OnRightClick = _ => AddPopup(50, move);
        move.OnExpand = () => AddPopup(51, [.. Enumerable.Range(0, Math.Max(named, 0)).Select(_ => MenuItem("Mail"))]);
        if (named < 0)
        {
            _tab.Rect = new UiaRect(0, 0, 0, 0);
        }

        var outcome = Actions.MoveToWindow(Read("Inbox"), Home, TimeSpan.FromMilliseconds(100));

        Assert.Equal((false, detail), (outcome.Outcome.Grouped, outcome.Outcome.Detail));
        Assert.Equal(Enumerable.Repeat(KeyEvent.Tap(Escape), escapes), _input.Keys);
    }

    [Fact]
    public void Ungroups_through_the_group_editor_and_a_settled_confirm()
    {
        var group = MoveIntoGroup(_strip, _tab, "Research");
        var ungroup = new FakeUiaElement("BubbleMenuItemButton", UiaControlType.Button, "Ungroup");
        _input.OnRightClick = _ => AddPopup(50, new FakeUiaElement("BubbleMenuItemButton", UiaControlType.Button, "Ungroup tabs"), ungroup);
        var confirm = new FakeUiaElement("MdTextButton", UiaControlType.Button, "Ungroup");
        TimeSpan? shownAt = null;
        TimeSpan? pressedAt = null;
        ungroup.OnInvoke = () =>
        {
            RemoveWindow(50);
            shownAt = _clock.Now - DateTimeOffset.UnixEpoch;
            _ = _root.Add(new FakeUiaElement("DialogClientView", UiaControlType.Window, "Ungroup tab group?").Add(
                new FakeUiaElement("MdTextButton", UiaControlType.Button, "Cancel"),
                confirm));
        };
        var presses = 0;
        confirm.OnInvoke = () =>
        {
            pressedAt ??= _clock.Now - DateTimeOffset.UnixEpoch;
            if (++presses == 1)
            {
                throw new UiaException("ignored while the dialog settles");
            }

            group.Remove(_tab);
            _ = _strip.Add(_tab);
        };

        var outcome = Actions.Ungroup(Read("Inbox"), "Research");

        Assert.Equal((true, "ungrouped"), (outcome.Grouped, outcome.Detail));
        Assert.Equal(2, confirm.Invokes);
        Assert.True(pressedAt - shownAt >= ChromeUiaActions.DialogSettle);
    }

    [Fact]
    public void Ungrouping_refuses_or_reports_a_group_that_stays()
    {
        var group = MoveIntoGroup(_strip, _tab, "Research");
        var seen = Read("Inbox");
        var header = (FakeUiaElement)group.FindAll(UiaScope.Children, new UiaCondition(ClassName: "TabGroupHeaderView"))[0];

        var renamed = Actions.Ungroup(seen, "Other");
        _win32.Foreground = Elsewhere;
        _input.OnSetForeground = null;
        var background = Actions.Ungroup(seen, "Research", TimeSpan.FromMilliseconds(100));
        _input.OnSetForeground = handle => _win32.Foreground = handle;
        AddPopup(50, MenuItem("Leftover"));
        var leftover = Actions.Ungroup(seen, "Research", TimeSpan.FromMilliseconds(100));
        RemoveWindow(50);
        header.Offscreen = true;
        var hidden = Actions.Ungroup(seen, "Research");
        header.Offscreen = false;
        var noEditor = Actions.Ungroup(seen, "Research", TimeSpan.FromMilliseconds(100));
        _input.OnRightClick = _ => AddPopup(50, new FakeUiaElement("BubbleMenuItemButton", UiaControlType.Button, "Ungroup"));
        var stays = Actions.Ungroup(seen, "Research", TimeSpan.FromMilliseconds(100));

        Assert.Equal("tab is no longer in a group called 'Other'", renamed.Detail);
        Assert.Equal("window is not in the foreground", background.Detail);
        Assert.Equal("a menu from the previous step is still open", leftover.Detail);
        Assert.Equal("group header is not visible where a click would land", hidden.Detail);
        Assert.Equal("group editor did not offer Ungroup", noEditor.Detail);
        Assert.Equal("group 'Research' is still there", stays.Detail);
        Assert.Single(_input.Keys, keys => keys.SequenceEqual(KeyEvent.Tap(Escape)));
    }

    [Fact]
    public void Dialog_buttons_and_points_read_nothing_from_a_window_that_will_not_read()
    {
        _uia.Roots[Window] = new FakeUiaElement("Gone") { Stale = true };

        Assert.Null(Actions.DialogButton(Window, "Ungroup tab group?", "ungroup"));
        Assert.Null(Actions.DialogButton(Elsewhere, "Ungroup tab group?", "ungroup"));
        Assert.False(Actions.IsAtPoint(new ChromeTab(new RuntimeId([1]), Window, 0, "Inbox", "Inbox", false, false, true, false), _tab, 1, 1));
        Assert.Null(Actions.TitledInGroup(Elsewhere, "Research", "Inbox"));
    }

    private (FakeUiaElement Root, FakeUiaElement Strip) AddWindow(long handle, string title)
    {
        _win32.Add(new FakeWindow(handle, title, ChromePid) { ClassName = ChromeUiaReader.WindowClass, Rect = new(0, 0, 800, 600), Root = handle });
        var root = new FakeUiaElement(ChromeUiaReader.WindowClass, UiaControlType.Window, title);
        var strip = new FakeUiaElement("TabStripView");
        _ = root.Add(new FakeUiaElement("BrowserView").Add(strip));
        _uia.Roots[handle] = root;
        return (root, strip);
    }

    private void AddPopup(long handle, params FakeUiaElement[] items)
    {
        _win32.Add(new FakeWindow(handle, string.Empty, ChromePid) { ClassName = ChromeUiaReader.WindowClass });
        _uia.Roots[handle] = new FakeUiaElement("MenuHost", UiaControlType.Window).Add(new FakeUiaElement("Menu").Add(items));
    }

    private void RemoveWindow(long handle) => _win32.Windows.RemoveAll(window => window.Handle == handle);

    /// <summary>Puts the tab into a group of that title, as Chrome lays one out: a raw-only view holding a header and its tabs.</summary>
    private static FakeUiaElement MoveIntoGroup(FakeUiaElement strip, FakeUiaElement tab, string title)
    {
        strip.Remove(tab);
        var group = new FakeUiaElement("TabGroupView") { RawOnly = true };
        _ = group.Add(new FakeUiaElement("TabGroupHeaderView", name: $" group {title} – 1 tab, •  {tab.NameValue} – Expanded"), tab);
        _ = strip.Add(group);
        return group;
    }

    private ChromeTab Read(string title) => Reader.ReadWindow(Window)!.Tabs.Single(tab => tab.Title == title);

    private static FakeUiaElement MenuItem(string name) => new("MenuItemView", UiaControlType.MenuItem, name);

    private static FakeUiaElement Tab(string name, bool selected = false) =>
        new("TabView", UiaControlType.TabItem, name) { Selected = selected };

    private sealed class NoShell : IChromeWindowShell
    {
        public WindowProfile Profile(long handle) => new(null, null);

        public WindowRect? RestoredRect(long handle) => null;
    }
}
