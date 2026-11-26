using Rig.Core.Chrome;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Chrome;

public sealed class LinkRouterTests
{
    private static readonly Uri Page = new("https://github.com/rtkelly13/rig/pull/1");

    private readonly List<ChromeWindow> _windows = [];
    private readonly FakeChromeGroupActions _actions = new();
    private readonly List<IReadOnlyList<string>> _launches = [];
    private int _nextTab = 500;

    /// <summary>Runs when Chrome is launched: the test decides where the new tab lands.</summary>
    private Action? _onLaunch;

    [Fact]
    public void Without_a_companion_window_it_opens_a_named_one()
    {
        _windows.Add(Window(1000, "Inbox - Google Chrome"));

        var outcome = Router().Open(Page, "Profile 1");

        Assert.True(outcome.Landed);
        var launched = Assert.Single(_launches);
        Assert.Equal(["--profile-directory=Profile 1", "--new-window", "--window-name=Rig links", Page.AbsoluteUri], launched);
        Assert.Empty(_actions.Log);
    }

    [Fact]
    public void A_tab_opened_in_the_companion_stays_there_and_the_focus_goes_back()
    {
        _windows.Add(Window(1000, "Inbox - Google Chrome"));
        _windows.Add(Window(2000, "Rig links - Google Chrome", "Profile 7"));
        _onLaunch = () => AddTab(2000);

        var outcome = Router().Open(Page, "Profile 1");

        Assert.Equal(new LinkOutcome(true, "opened in Rig links"), outcome);
        Assert.Equal(["--profile-directory=Profile 7", Page.AbsoluteUri], Assert.Single(_launches));
        Assert.Equal(["front 2000", "back 2000"], _actions.Log);
    }

    [Fact]
    public void A_tab_that_lands_elsewhere_is_moved_into_the_companion()
    {
        _windows.Add(Window(1000, "Inbox - Google Chrome"));
        _windows.Add(Window(2000, "Rig links - Google Chrome"));
        _onLaunch = () => AddTab(1000);

        var outcome = Router().Open(Page, "Profile 1");

        Assert.Equal(new LinkOutcome(true, "moved into Rig links"), outcome);
        Assert.Contains("move new to 2000", _actions.Log);
    }

    [Fact]
    public void A_failed_move_is_reported()
    {
        _windows.Add(Window(1000, "Inbox - Google Chrome"));
        _windows.Add(Window(2000, "Rig links - Google Chrome"));
        _actions.MoveFailure = "drag refused";
        _onLaunch = () => AddTab(1000);

        var outcome = Router().Open(Page, "Profile 1");

        Assert.False(outcome.Landed);
        Assert.Contains("drag refused", outcome.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "launched, but the new tab never showed up")]
    [InlineData(true, "Rig links would not come to the front; Chrome chose the window")]
    public void A_tab_that_never_appears_is_reported(bool refuses, string detail)
    {
        _windows.Add(Window(2000, "Rig links - Google Chrome"));
        if (refuses)
        {
            _ = _actions.Refuses.Add(2000);
        }

        var outcome = Router().Open(Page, "Profile 1");

        Assert.Equal(new LinkOutcome(false, detail), outcome);
    }

    [Theory]
    [InlineData("file:///C:/secrets.txt")]
    [InlineData("javascript:alert(1)")]
    public void Only_web_pages_are_opened(string address)
    {
        Assert.Throws<ArgumentException>(() => Router().Open(new Uri(address), "Profile 1"));
        Assert.Empty(_launches);
    }

    [Fact]
    public void The_companion_is_the_oldest_window_named_for_it()
    {
        ChromeWindow[] windows = [Window(1, "Docs - Google Chrome"), Window(2, "rig links - Google Chrome"), Window(3, "Rig links")];

        Assert.Equal(2, LinkRouter.FindCompanion(windows)?.Handle);
        Assert.Null(LinkRouter.FindCompanion([windows[0]]));
    }

    private LinkRouter Router() =>
        new(
            () => [.. _windows],
            _actions,
            args =>
            {
                _launches.Add(args);
                _onLaunch?.Invoke();
            },
            _ => { });

    private static ChromeWindow Window(long handle, string title, string profile = "Profile 1") =>
        new(handle, title, profile, null, handle, false, 1, [Tab(handle, 1, "old")]);

    private static ChromeTab Tab(long handle, int id, string title, bool selected = false) =>
        new(new RuntimeId([42, (int)handle, id]), handle, 0, title, title, selected, false, true, false);

    private void AddTab(long handle)
    {
        var index = _windows.FindIndex(window => window.Handle == handle);
        var window = _windows[index];
        _windows[index] = window with { Tabs = [.. window.Tabs, Tab(handle, ++_nextTab, "new", selected: true)] };
    }
}
