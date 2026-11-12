using Rig.Core.Chrome;
using Rig.Core.Chrome.Windows;
using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;
using Rig.Core.Desktop.Windows.Uia;
using Rig.Core.Tests.Desktop.Windows;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Chrome;

public sealed class ChromeUiaActionsTests
{
    private const int ChromePid = 10;
    private const long Window = 1;
    private readonly FakeWin32Windows _win32 = new();
    private readonly FakeUiAutomation _uia = new();
    private readonly FakeWin32Actions _input = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);
    private readonly FakeUiaElement _root;
    private readonly FakeUiaElement _strip;
    private ChromeUiaReader? _reader;

    public ChromeUiaActionsTests()
    {
        _win32.Processes[ChromePid] = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        _root = AddWindow(Window, "W - Google Chrome");
        _strip = new FakeUiaElement("TabStripView");
        _ = _root.Add(new FakeUiaElement("BrowserView").Add(_strip));
    }

    private ChromeUiaReader Reader => _reader ??= new(_uia, _win32, _win32, new NoShell(), _clock.Advance);

    private ChromeUiaActions Actions => new(Reader, _uia, _win32, _input, _clock, _clock.Advance);

    [Fact]
    public void Closes_a_tab_by_selecting_it_and_pressing_its_close_button()
    {
        var tab = Tab("Inbox");
        var other = Tab("Docs", selected: true);
        _ = _strip.Add(tab, other);
        var button = CloseButtonOnSelect(tab);
        button.OnInvoke = () => _strip.Remove(tab);

        var outcome = Actions.CloseTab(Read("Inbox"));

        Assert.Equal((true, "closed"), (outcome.Closed, outcome.Detail));
        Assert.Equal(1, tab.Selects);
        Assert.Equal(1, button.Invokes);
    }

    [Fact]
    public void Refuses_a_tab_that_has_gone_or_whose_title_changed()
    {
        var tab = Tab("Inbox");
        _ = _strip.Add(tab);
        var seen = Read("Inbox");

        tab.NameValue = "Inbox (3)";
        var renamed = Actions.CloseTab(seen);
        _strip.Remove(tab);
        var gone = Actions.CloseTab(seen);

        Assert.Equal((false, "title changed to 'Inbox (3)'"), (renamed.Closed, renamed.Detail));
        Assert.Equal((false, "tab no longer present"), (gone.Closed, gone.Detail));
        Assert.Equal(0, tab.Selects);
    }

    [Fact]
    public void Refuses_when_the_close_button_never_appears()
    {
        var tab = Tab("Inbox");
        _ = _strip.Add(tab);

        var outcome = Actions.CloseTab(Read("Inbox"), TimeSpan.FromSeconds(1));

        Assert.Equal((false, "close button did not appear after selecting"), (outcome.Closed, outcome.Detail));
        Assert.True(outcome.Elapsed >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_stale_tab_reads_as_no_close_button()
    {
        var tab = Tab("Inbox");
        _ = _strip.Add(tab);
        var seen = Read("Inbox");
        tab.OnSelect = () => tab.Stale = true;

        var outcome = Actions.CloseTab(seen, TimeSpan.FromMilliseconds(100));

        Assert.Equal("close button did not appear after selecting", outcome.Detail);
    }

    [Fact]
    public void Refuses_when_the_tab_loses_selection_or_the_button_is_not_close()
    {
        var unselected = Tab("Inbox");
        _ = unselected.Add(new FakeUiaElement("TabCloseButton", UiaControlType.Button, "Close"));
        var foreign = Tab("Docs");
        var button = CloseButtonOnSelect(foreign);
        button.NameValue = "Schließen";
        _ = _strip.Add(unselected, foreign);

        var lost = Actions.CloseTab(Read("Inbox"));
        var odd = Actions.CloseTab(Read("Docs"));

        Assert.Equal("tab lost selection before close", lost.Detail);
        Assert.Equal("unexpected close button name 'Schließen'", odd.Detail);
        Assert.Equal(0, button.Invokes);
    }

    [Fact]
    public void Reports_a_tab_still_present_after_the_press()
    {
        var tab = Tab("Inbox");
        _ = _strip.Add(tab);
        _ = CloseButtonOnSelect(tab);

        var outcome = Actions.CloseTab(Read("Inbox"));

        Assert.Equal((false, "invoked close but the tab is still present"), (outcome.Closed, outcome.Detail));
    }

    [Fact]
    public void Confirms_deleting_the_group_when_closing_its_last_tab()
    {
        var tab = Tab("Paper");
        var group = new FakeUiaElement("TabGroupView") { RawOnly = true };
        _ = group.Add(new FakeUiaElement("TabGroupHeaderView", name: " group Research – 1 tab, •  Paper – Expanded"), tab);
        _ = _strip.Add(group);
        _ = CloseButtonOnSelect(tab);
        AddChildHost(77, new FakeUiaElement("Other"));
        AddChildHost(78, new FakeUiaElement("RootView").Add(new FakeUiaElement("Label", name: "Something else")));
        _win32.Children[Window].Add(79);
        var stale = new FakeUiaElement("RootView") { Stale = true };
        AddChildHost(80, stale);
        var delete = new FakeUiaElement("MdTextButton", UiaControlType.Button, "Delete group");
        delete.OnInvoke = () => group.Remove(tab);
        AddChildHost(81, new FakeUiaElement("RootView").Add(
            new FakeUiaElement("Label", name: "Close tab and delete group?"),
            new FakeUiaElement("MdTextButton", UiaControlType.Button, "Cancel"),
            delete));

        var outcome = Actions.CloseTab(Read("Paper"));

        Assert.Equal("closed", outcome.Detail);
        Assert.Equal(1, delete.Invokes);
    }

    [Fact]
    public void Restores_each_windows_selection_or_its_fallback()
    {
        var inbox = Tab("Inbox", selected: true);
        var docs = Tab("Docs");
        _ = _strip.Add(inbox, docs);
        var second = AddWindow(2, "X - Google Chrome");
        var gone = Tab("Gone", selected: true);
        var fallback = Tab("Fallback");
        var secondStrip = new FakeUiaElement("TabStripView").Add(gone, fallback);
        _ = second.Add(secondStrip);
        var third = AddWindow(3, "Y - Google Chrome");
        _ = third.Add(new FakeUiaElement("TabStripView").Add(Tab("Unselected")));
        var before = Reader.ListWindows();
        var fallbackTab = before.Single(window => window.Handle == 2).Tabs[1];
        secondStrip.Remove(gone);

        var restored = Actions.RestoreSelection(before, new Dictionary<long, ChromeTab> { [2] = fallbackTab });

        Assert.Equal("Inbox", restored[Window]?.Title);
        Assert.Equal("Fallback", restored[2]?.Title);
        Assert.Null(restored[3]);
        Assert.Equal((1, 1), (inbox.Selects, fallback.Selects));
        Assert.False(Actions.SelectTab(before.Single(window => window.Handle == 2).Tabs[0]));
    }

    [Fact]
    public void Reads_saved_groups_off_the_bookmarks_bar()
    {
        var bar = SavedBar(" Google group – Opened", "Bookmark", "News group – Closed");

        var groups = Actions.SavedGroups(Window, "Default");

        Assert.Equal(
            [new SavedGroup("Default", "Google", true, 0), new SavedGroup("Default", "News", false, 1)],
            groups);
        Assert.Empty(Actions.SavedGroups(99, "Default"));
        bar.ClassNameValue = "Hidden";
        Assert.Empty(Actions.SavedGroups(Window, "Default"));
        _root.Stale = true;
        Assert.Empty(Actions.SavedGroups(Window, "Default"));
    }

    [Fact]
    public void Deleting_a_saved_group_refuses_without_a_closed_copy_or_a_second_copy()
    {
        _ = SavedBar("Google group – Opened", "News group – Closed");

        var noClosed = Actions.DeleteSavedGroup(Window, "Default", "Google");
        var only = Actions.DeleteSavedGroup(Window, "Default", "News");

        Assert.Equal("no closed saved group called 'Google' on the bar", noClosed.Detail);
        Assert.Equal("the only saved copy of 'News'; kept", only.Detail);
        Assert.Empty(_input.Keys);
    }

    [Fact]
    public void Deleting_a_saved_group_waits_for_an_open_menu_to_close_first()
    {
        _ = SavedBar("Google group – Opened", "Google group – Closed");
        _ = AddPopup(50, new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Leftover"));

        var outcome = Actions.DeleteSavedGroup(Window, "Default", "Google", TimeSpan.FromMilliseconds(200));

        Assert.Equal((false, "a menu from the previous step is still open"), (outcome.Deleted, outcome.Detail));
    }

    [Fact]
    public void Deletes_the_rightmost_closed_copy_through_its_menu_and_a_settled_confirm()
    {
        var bar = SavedBar("Google group – Opened", "Google group – Closed", "Google group – Closed");
        var chips = bar.FindAll(UiaScope.Descendants, new UiaCondition()).Cast<FakeUiaElement>().ToList();
        var delete = new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, " Delete group ");
        chips[2].OnContextMenu = () => _ = AddPopup(
            50,
            new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Open group"),
            delete,
            new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Tabs in group"),
            new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, " Paper "),
            new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, " "),
            new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Notes"));
        var confirm = new FakeUiaElement("MdTextButton", UiaControlType.Button, "Delete");
        TimeSpan? shownAt = null;
        TimeSpan? pressedAt = null;
        delete.OnInvoke = () =>
        {
            _ = _win32.Windows.RemoveAll(window => window.Handle == 50);
            shownAt = _clock.Now - DateTimeOffset.UnixEpoch;
            AddChildHost(81, new FakeUiaElement("RootView").Add(
                new FakeUiaElement("Dialog", UiaControlType.Window, "Delete group?").Add(
                    new FakeUiaElement("MdTextButton", UiaControlType.Button, "Cancel"),
                    confirm)));
        };
        confirm.OnInvoke = () =>
        {
            pressedAt = _clock.Now - DateTimeOffset.UnixEpoch;
            bar.Remove(chips[2]);
        };

        var outcome = Actions.DeleteSavedGroup(Window, "Default", "Google");

        Assert.Equal((true, "deleted"), (outcome.Deleted, outcome.Detail));
        Assert.Equal(["Paper", "Notes"], outcome.Tabs);
        Assert.Equal((0, 1), (chips[1].ContextMenus, chips[2].ContextMenus));
        Assert.Equal(1, confirm.Invokes);
        Assert.True(pressedAt - shownAt >= ChromeUiaActions.DialogSettle);
        Assert.Empty(_input.Keys);
    }

    [Fact]
    public void A_menu_without_delete_group_is_escaped()
    {
        var bar = SavedBar("Google group – Opened", "Google group – Closed");
        var chip = (FakeUiaElement)bar.FindAll(UiaScope.Descendants, new UiaCondition())[1];
        chip.OnContextMenu = () => _ = AddPopup(50, new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Delete group"));

        var outcome = Actions.DeleteSavedGroup(Window, "Default", "Google", TimeSpan.FromMilliseconds(200));

        Assert.Equal("the chip's menu did not offer Delete group", outcome.Detail);
        Assert.Equal([KeyEvent.Tap(0x1B)], _input.Keys);
    }

    [Fact]
    public void A_chip_that_stays_is_reported_and_its_menu_escaped()
    {
        var bar = SavedBar("Google group – Opened", "Google group – Closed");
        var chip = (FakeUiaElement)bar.FindAll(UiaScope.Descendants, new UiaCondition())[1];
        var delete = new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Delete group");
        chip.OnContextMenu = () => _ = AddPopup(
            50,
            new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Open group"),
            delete);
        var confirm = new FakeUiaElement("MdTextButton", UiaControlType.Button, "Delete group");
        confirm.OnInvoke = () => throw new UiaException("gone");
        delete.OnInvoke = () => AddChildHost(81, new FakeUiaElement("Dialog", UiaControlType.Window).Add(confirm));

        var outcome = Actions.DeleteSavedGroup(Window, "Default", "Google");

        Assert.Equal((false, "saved group 'Google' is still on the bar"), (outcome.Deleted, outcome.Detail));
        Assert.True(outcome.Elapsed >= ChromeUiaActions.SavedDeleteTimeout);
        Assert.True(confirm.Invokes > 1);
        Assert.Single(_input.Keys);
    }

    [Fact]
    public void Popups_are_the_processes_visible_windows_other_than_titled_browser_windows()
    {
        _ = AddPopup(50, new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Mine"));
        _win32.Add(new FakeWindow(51, string.Empty, ChromePid) { Visible = false });
        _uia.Roots[51] = new FakeUiaElement("Menu").Add(new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Hidden"));
        _win32.Add(new FakeWindow(52, string.Empty, 20));
        _uia.Roots[52] = new FakeUiaElement("Menu").Add(new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Other"));
        _ = _root.Add(new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "In the browser"));
        AddPopup(53, new FakeUiaElement("MenuItemView", UiaControlType.MenuItem, "Stale")).Stale = true;
        _win32.Add(new FakeWindow(54, string.Empty, ChromePid));

        var items = Actions.PopupElements(ChromePid, UiaControlType.MenuItem);

        Assert.Equal(["Mine"], items.Select(item => ((FakeUiaElement)item).NameValue));
        Assert.Null(Actions.MenuItem(ChromePid, name => name == "other"));
        Assert.True(Actions.MenusClosed(30, TimeSpan.Zero));
        Assert.False(Actions.MenusClosed(20, TimeSpan.Zero));
    }

    [Fact]
    public void Polls_until_the_probe_answers_or_the_timeout_passes()
    {
        var calls = 0;

        Assert.Equal("done", Actions.Poll(() => ++calls == 3 ? "done" : null, TimeSpan.FromSeconds(1)));
        Assert.Null(Actions.Poll<string>(() => null, TimeSpan.FromMilliseconds(120)));
        Assert.False(Actions.PollUntil(() => false, TimeSpan.Zero));
        Assert.Equal(ChromeUiaActions.PollInterval * 5, _clock.Now - DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Finds_a_confirm_button_in_the_windows_own_tree_and_skips_stale_hosts()
    {
        _win32.Children[Window] = [90];
        _uia.Roots[90] = new FakeUiaElement("Host") { Stale = true };
        _ = _root.Add(new FakeUiaElement("Dialog", UiaControlType.Window).Add(
            new FakeUiaElement("MdTextButton", UiaControlType.Button, " OK ")));

        Assert.NotNull(Actions.ConfirmButton(Window, new HashSet<string> { "ok" }));
        Assert.Null(Actions.ConfirmButton(Window, new HashSet<string> { "delete" }));
    }

    private FakeUiaElement AddWindow(long handle, string title)
    {
        _win32.Add(new FakeWindow(handle, title, ChromePid) { ClassName = ChromeUiaReader.WindowClass, Rect = new Rect(0, 0, 800, 600) });
        var root = new FakeUiaElement(ChromeUiaReader.WindowClass, UiaControlType.Window, title);
        _uia.Roots[handle] = root;
        return root;
    }

    private FakeUiaElement AddPopup(long handle, params FakeUiaElement[] items)
    {
        _win32.Add(new FakeWindow(handle, string.Empty, ChromePid) { ClassName = ChromeUiaReader.WindowClass });
        var popup = new FakeUiaElement("MenuHost", UiaControlType.Window).Add(new FakeUiaElement("Menu").Add(items));
        _uia.Roots[handle] = popup;
        return popup;
    }

    private void AddChildHost(long handle, FakeUiaElement child)
    {
        if (!_win32.Children.TryGetValue(Window, out var children))
        {
            children = [];
            _win32.Children[Window] = children;
        }

        children.Add(handle);
        _uia.Roots[handle] = new FakeUiaElement("Host").Add(child);
    }

    private FakeUiaElement SavedBar(params string[] chips)
    {
        var bar = new FakeUiaElement("SavedTabGroupBar");
        _ = bar.Add([.. chips.Select(name => new FakeUiaElement("SavedTabGroupButton", UiaControlType.Button, name))]);
        _ = _root.Add(new FakeUiaElement("BookmarkBarView").Add(bar));
        return bar;
    }

    private ChromeTab Read(string title) => Reader.ReadWindow(Window)!.Tabs.Single(tab => tab.Title == title);

    private static FakeUiaElement Tab(string name, bool selected = false) =>
        new("TabView", UiaControlType.TabItem, name) { Selected = selected };

    /// <summary>Selecting the tab selects it and shows its close button, as Chrome does for the active tab.</summary>
    private static FakeUiaElement CloseButtonOnSelect(FakeUiaElement tab)
    {
        var button = new FakeUiaElement("TabCloseButton", UiaControlType.Button, "Close");
        tab.OnSelect = () =>
        {
            tab.Selected = true;
            if (tab.FindAll(UiaScope.Children, new UiaCondition(ClassName: "TabCloseButton")).Count == 0)
            {
                _ = tab.Add(button);
            }
        };
        return button;
    }

    private sealed class NoShell : IChromeWindowShell
    {
        public WindowProfile Profile(long handle) => new(null, null);

        public WindowRect? RestoredRect(long handle) => null;
    }
}
