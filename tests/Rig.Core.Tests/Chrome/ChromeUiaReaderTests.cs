using Rig.Core.Chrome;
using Rig.Core.Chrome.Windows;
using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows.Uia;
using Rig.Core.Tests.Desktop.Windows;

namespace Rig.Core.Tests.Chrome;

public sealed class ChromeUiaReaderTests
{
    private const int ChromePid = 10;
    private readonly FakeWin32Windows _win32 = new();
    private readonly FakeUiAutomation _uia = new();
    private readonly FakeChromeShell _shell = new();
    private readonly List<TimeSpan> _sleeps = [];

    public ChromeUiaReaderTests()
    {
        _win32.Processes[ChromePid] = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        _win32.Processes[20] = @"C:\Windows\notepad.exe";
        _win32.AddMonitor(new Rect(-1920, 0, 1920, 1080), new Rect(-1920, 0, 1920, 1040));
    }

    [Fact]
    public void Lists_browser_windows_oldest_first_with_their_tabs()
    {
        var younger = AddWindow(1, "Younger - Google Chrome", rootViewId: 900);
        _ = Strip(younger, Tab("Inbox", selected: true));
        var older = AddWindow(2, "Older - Google Chrome", rootViewId: 300);
        _ = Strip(older, Tab("Docs"), Tab("News - Memory usage - 120 MB"));
        _shell.Profiles[2] = new WindowProfile("Profile 4", "Ryan (personal)");

        var windows = Reader().ListWindows();

        Assert.Equal([2L, 1L], windows.Select(window => window.Handle));
        var first = windows[0];
        Assert.Equal(("Profile 4", "Ryan (personal)", 300L, 1), (first.ProfileDir, first.ProfileName, first.AgeRank, first.DesktopNumber));
        Assert.Equal(["Docs", "News"], first.Tabs.Select(tab => tab.Title));
        Assert.Equal([0, 1], first.Tabs.Select(tab => tab.Index));
        Assert.Equal(120, first.Tabs[1].MemoryMb);
        Assert.All(first.Tabs, tab => Assert.Equal(2L, tab.WindowHandle));
        Assert.Equal("Inbox", windows[1].SelectedTab?.Title);
        Assert.Equal(new ScreenPoint(-1920, 0), first.Monitor);
        Assert.Equal(new WindowRect(10, 20, 810, 620), first.Rect);
    }

    [Fact]
    public void Skips_other_classes_untitled_windows_other_processes_and_windows_without_a_strip()
    {
        _ = Strip(AddWindow(1, "Real - Google Chrome"), Tab("Kept"));
        _ = Strip(AddWindow(2, "   "), Tab("Untitled"));
        _ = Strip(AddWindow(3, "Notepad", pid: 20), Tab("Other process"));
        _ = Strip(AddWindow(4, "Other class", className: "Notepad"), Tab("Other class"));
        _ = AddWindow(5, "Popup - Google Chrome");
        _win32.Add(new FakeWindow(6, "No element", ChromePid) { ClassName = ChromeUiaReader.WindowClass });

        var reader = Reader();

        Assert.Equal([1L, 5L, 6L], reader.BrowserWindows());
        Assert.Equal([1L], reader.ListWindows().Select(window => window.Handle));
    }

    [Fact]
    public void A_window_without_a_root_view_id_ranks_behind_every_real_one_in_z_order()
    {
        _ = Strip(AddWindow(1, "A - Google Chrome", rootViewId: null), Tab("a"));
        _ = Strip(AddWindow(2, "B - Google Chrome", rootViewId: null), Tab("b"));
        _ = Strip(AddWindow(3, "C - Google Chrome", rootViewId: 5), Tab("c"));

        var windows = Reader().ListWindows();

        Assert.Equal([3L, 1L, 2L], windows.Select(window => window.Handle));
        Assert.Equal([5L, ChromeUiaReader.UnrankedAge, ChromeUiaReader.UnrankedAge + 1], windows.Select(window => window.AgeRank));
    }

    [Fact]
    public void Reads_pinning_from_the_name_or_the_container_but_not_an_unpinned_container()
    {
        var root = AddWindow(1, "W - Google Chrome");
        var strip = Strip(root);
        _ = strip.Add(
            new FakeUiaElement("PinnedTabContainerView").Add(Tab("In container")),
            new FakeUiaElement("UnpinnedTabContainerView").Add(Tab("Unpinned")),
            Tab("Pinned label - Pinned"),
            Tab("Plain"));

        var tabs = Reader().ListWindows().Single().Tabs;

        Assert.Equal(
            [("In container", true), ("Unpinned", false), ("Pinned label", true), ("Plain", false)],
            tabs.Select(tab => (tab.Title, tab.Pinned)));
    }

    [Fact]
    public void Reads_a_tabs_group_from_its_group_view_header_in_the_raw_view()
    {
        var strip = Strip(AddWindow(1, "W - Google Chrome"));
        var group = new FakeUiaElement("TabGroupView") { RawOnly = true };
        _ = group.Add(new FakeUiaElement("TabGroupHeaderView", name: " group Research – 1 tab, •  Paper – Expanded"), Tab("Paper"));
        var unnamed = new FakeUiaElement("TabGroupView") { RawOnly = true };
        _ = unnamed.Add(new FakeUiaElement("TabGroupHeaderView", name: "not a header name"), Tab("Odd"));
        var headless = new FakeUiaElement("TabGroupView") { RawOnly = true };
        _ = headless.Add(Tab("Headless"));
        _ = strip.Add(group, unnamed, headless, Tab("Loose"));

        var tabs = Reader().ListWindows().Single().Tabs;

        var paper = tabs.Single(tab => tab.Title == "Paper");
        Assert.Equal(new TabGroup(new RuntimeId(group.Id), "Research"), paper.Group);
        Assert.All(tabs.Where(tab => tab.Title != "Paper"), tab => Assert.Null(tab.Group));
    }

    [Fact]
    public void The_strip_search_never_descends_into_web_content()
    {
        var root = AddWindow(1, "W - Google Chrome");
        var content = new FakeUiaElement("RenderWidgetHostViewAura", UiaControlType.Document);
        _ = content.Add(new FakeUiaElement("TabStripView").Add(Tab("In a page")));
        _ = root.Add(content);

        Assert.Empty(Reader().ListWindows());
    }

    [Fact]
    public void A_minimised_window_reports_its_restored_rect()
    {
        _ = Strip(AddWindow(1, "W - Google Chrome", minimized: true), Tab("a"));
        _shell.Restored[1] = new WindowRect(1, 2, 3, 4);

        var window = Reader().ListWindows().Single();

        Assert.True(window.Minimized);
        Assert.Equal(new WindowRect(1, 2, 3, 4), window.Rect);
    }

    [Fact]
    public void Rereads_a_window_whose_tabs_go_stale_mid_read()
    {
        var tab = Tab("Flaky");
        tab.FailingNameReads = 2;
        _ = Strip(AddWindow(1, "W - Google Chrome"), tab);

        var window = Reader().ListWindows().Single();

        Assert.Equal("Flaky", window.Tabs.Single().Title);
        Assert.Equal([ChromeUiaReader.ReadRetryPause, ChromeUiaReader.ReadRetryPause], _sleeps);
    }

    [Fact]
    public void A_window_that_never_reads_cleanly_fails_the_listing_but_not_a_single_read()
    {
        var tab = Tab("Flaky");
        tab.FailingNameReads = int.MaxValue;
        _ = Strip(AddWindow(1, "W - Google Chrome"), tab);
        var reader = Reader();

        var error = Assert.Throws<ChromeReadException>(() => reader.ListWindows());

        Assert.Contains("3 attempts", error.Message, StringComparison.Ordinal);
        Assert.IsType<UiaException>(error.InnerException);
        Assert.Null(reader.ReadWindow(1));
    }

    [Fact]
    public void A_window_that_closes_mid_read_is_simply_gone()
    {
        var tab = Tab("Closing");
        tab.FailingNameReads = 1;
        _ = Strip(AddWindow(1, "W - Google Chrome"), tab);
        var reader = Reader();
        _ = Assert.Single(reader.BrowserWindows());
        _win32.Windows.Clear();

        Assert.Null(reader.ReadWindow(1));
        Assert.Empty(_sleeps);
    }

    [Fact]
    public void Finds_a_tabs_live_element_by_runtime_id()
    {
        var wanted = Tab("Wanted");
        _ = Strip(AddWindow(1, "W - Google Chrome"), Tab("Other"), wanted);
        var reader = Reader();
        var tab = reader.ListWindows().Single().Tabs[1];

        Assert.Same(wanted, reader.FindTabElement(tab));

        _ = _uia.Roots.Remove(1);
        Assert.Null(reader.FindTabElement(tab));
        Assert.Null(reader.FindTabElement(tab with { WindowHandle = 99 }));
    }

    [Fact]
    public void Caches_the_strip_while_it_stays_live_and_finds_it_again_once_stale()
    {
        var root = AddWindow(1, "W - Google Chrome");
        var strip = Strip(root, Tab("a"));
        var reader = Reader();

        Assert.Same(strip, reader.FindTabStrip(1, root));
        root.Add(new FakeUiaElement("Decoy"));
        Assert.Same(strip, reader.FindTabStrip(1, root));

        strip.Stale = true;
        var fresh = new FakeUiaElement("TabStripView");
        _ = root.Add(fresh);
        _ = Assert.Throws<UiaException>(() => strip.ClassName);
        Assert.Same(fresh, reader.FindTabStrip(1, new FakeUiaElement("Root").Add(fresh)));
    }

    [Fact]
    public void Read_settled_retries_only_as_many_times_as_asked()
    {
        var calls = 0;
        var retries = 0;

        var error = Assert.Throws<ChromeReadException>(() => ChromeUiaReader.ReadSettled<string>(
            () =>
            {
                calls++;
                throw new UiaException("stale");
            },
            () => true,
            () => retries++,
            _sleeps.Add,
            attempts: 2));

        Assert.Equal((2, 2), (calls, retries));
        Assert.Single(_sleeps);
        Assert.Contains("stale", error.Message, StringComparison.Ordinal);
        Assert.Equal("read", ChromeUiaReader.ReadSettled(() => "read", () => true, () => { }, _sleeps.Add));
    }

    private ChromeUiaReader Reader() => new(_uia, _win32, _win32, _shell, _sleeps.Add);

    private FakeUiaElement AddWindow(
        long handle,
        string title,
        int pid = ChromePid,
        string className = ChromeUiaReader.WindowClass,
        int? rootViewId = 100,
        bool minimized = false)
    {
        _win32.Add(new FakeWindow(handle, title, pid) { ClassName = className, Minimized = minimized, Rect = new Rect(10, 20, 800, 600) });
        var root = new FakeUiaElement(className, UiaControlType.Window, title);
        var rootView = new FakeUiaElement("BrowserRootView");
        if (rootViewId is { } id)
        {
            rootView.Id = [42, id];
        }
        else
        {
            rootView.Id = [];
        }

        _ = root.Add(rootView);
        _uia.Roots[handle] = root;
        return root;
    }

    private static FakeUiaElement Strip(FakeUiaElement root, params FakeUiaElement[] tabs)
    {
        var strip = new FakeUiaElement("TabStripView");
        _ = strip.Add(tabs);
        _ = root.Add(new FakeUiaElement("BrowserView").Add(new FakeUiaElement("TopContainerView").Add(strip)));
        return strip;
    }

    private static FakeUiaElement Tab(string name, bool selected = false) =>
        new("TabView", UiaControlType.TabItem, name) { Selected = selected };

    private sealed class FakeChromeShell : IChromeWindowShell
    {
        public Dictionary<long, WindowProfile> Profiles { get; } = [];

        public Dictionary<long, WindowRect> Restored { get; } = [];

        public WindowProfile Profile(long handle) => Profiles.GetValueOrDefault(handle) ?? new(null, null);

        public WindowRect? RestoredRect(long handle) => Restored.GetValueOrDefault(handle);
    }
}
