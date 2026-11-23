using Rig.Core.Chrome;
using Rig.Core.Chrome.Extension;
using Rig.Core.Chrome.MacOS;

namespace Rig.Core.Tests.Chrome.Extension;

public sealed class ChromeExtensionTests
{
    private static readonly Dictionary<string, ProfileInfo> Profiles = new(StringComparer.Ordinal)
    {
        ["Default"] = new ProfileInfo("Work", UserName: "me@work.example"),
        ["Profile 2"] = new ProfileInfo("Me", UserName: "me@example.com"),
    };

    private static ChromeExtension Driver(params FakeExtension[] extensions) =>
        new(new FakeBridge(extensions), _ => Profiles, TimeProvider.System);

    private static FakeExtension TwoWindows() => new FakeExtension()
        .Window(1, (11, "rig", "https://github.com/rtkelly13/rig"), (12, "Docs", "https://example.com/docs"))
        .Window(2, (21, "Issues", "https://github.com/rtkelly13/rig/issues"));

    [Fact]
    public void Reads_windows_and_tabs_into_the_profile_the_account_names()
    {
        var windows = Driver(TwoWindows()).ListWindows();

        Assert.Equal([1L, 2L], windows.Select(window => window.Handle));
        var first = windows[0];
        Assert.Equal(("Profile 2", "Me"), (first.ProfileDir, first.ProfileName));
        Assert.Null(first.ProfileScope);
        Assert.Equal(new WindowRect(0, 0, 800, 600), first.Rect);
        Assert.Equal(["rig", "Docs"], first.Tabs.Select(tab => tab.Title));
        Assert.Equal(new RuntimeId([11]), first.Tabs[0].RuntimeId);
        Assert.Equal(new Uri("https://example.com/docs"), first.Tabs[1].Url);
        Assert.True(first.Tabs[0].Loaded);
    }

    [Fact]
    public void A_profile_with_no_account_is_the_browsers_only_profile_or_its_own_scope()
    {
        var only = new ChromeExtension(new FakeBridge(new FakeExtension(email: "").Window(1)), _ => new Dictionary<string, ProfileInfo> { ["Profile 2"] = new("Me") }, TimeProvider.System);
        Assert.Equal("Profile 2", only.ListWindows()[0].ProfileDir);

        var window = Driver(new FakeExtension(email: "", socket: "/run/7.sock").Window(1)).ListWindows()[0];
        Assert.Null(window.ProfileDir);
        Assert.Equal("extension:7", window.ProfileScope);
    }

    [Fact]
    public void Group_members_carry_the_group()
    {
        var tab = Driver(TwoWindows().Group(1, "rig", 11)).ListWindows()[0].Tabs[0];

        Assert.Equal("rig", tab.Group?.Title);
    }

    [Fact]
    public void Reading_with_nothing_connected_says_how_to_connect()
    {
        var error = Assert.Throws<ChromeReadException>(() => Driver().ListWindows());

        Assert.Contains("extension install", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Closes_a_tab_and_confirms_it_is_gone()
    {
        var extension = TwoWindows();
        var driver = Driver(extension);
        var tab = driver.ListWindows()[0].Tabs[1];

        var outcome = driver.CloseTab(tab);

        Assert.True(outcome.Closed, outcome.Detail);
        Assert.DoesNotContain(driver.ListWindows()[0].Tabs, current => current.RuntimeId.Equals(tab.RuntimeId));
    }

    [Fact]
    public void Refuses_to_close_a_tab_whose_page_changed_since_it_was_read()
    {
        var extension = TwoWindows();
        var driver = Driver(extension);
        var tab = driver.ListWindows()[0].Tabs[1];
        extension.Retitle(12, "Something else");

        var outcome = driver.CloseTab(tab);

        Assert.False(outcome.Closed);
        Assert.Contains("changed", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(ExtensionProtocol.CloseCommand, extension.Commands);
    }

    [Fact]
    public void An_extension_error_is_a_failed_step_not_a_crash()
    {
        var extension = TwoWindows();
        var driver = Driver(extension);
        var tab = driver.ListWindows()[0].Tabs[1];
        extension.Refuse = "No tab with id: 12";

        var outcome = driver.CloseTab(tab);

        Assert.False(outcome.Closed);
        Assert.Contains("No tab with id", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Creates_a_group_then_joins_it_from_another_window()
    {
        var driver = Driver(TwoWindows());
        var windows = driver.ListWindows();

        var created = driver.GroupTab(windows[0].Tabs[0], "rig", isNew: true, homeHandle: 1);
        var joined = driver.GroupTab(windows[1].Tabs[0], "rig", isNew: false, homeHandle: 1);

        Assert.True(created.Grouped, created.Detail);
        Assert.True(joined.Grouped, joined.Detail);
        var home = driver.ListWindows().Single(window => window.Handle == 1);
        Assert.Equal(["Issues", "rig"], home.Tabs.Where(tab => tab.Group?.Title == "rig").Select(tab => tab.Title).Order(StringComparer.Ordinal));
        Assert.Single(home.Tabs.Select(tab => tab.Group).Where(group => group is not null).Distinct());
    }

    [Fact]
    public void A_new_group_needs_a_title_no_group_in_the_profile_has()
    {
        var driver = Driver(TwoWindows().Group(2, "rig", 21));
        var tab = driver.ListWindows()[0].Tabs[0];

        var outcome = driver.GroupTab(tab, "rig", isNew: true, homeHandle: 1);

        Assert.False(outcome.Grouped);
        Assert.Contains("unique title", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Joining_needs_the_group_in_the_home_window()
    {
        var driver = Driver(TwoWindows().Group(2, "rig", 21));
        var tab = driver.ListWindows()[0].Tabs[0];

        var outcome = driver.GroupTab(tab, "rig", isNew: false, homeHandle: 1);

        Assert.False(outcome.Grouped);
        Assert.Contains("different window", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_to_group_a_tab_already_in_a_group()
    {
        var driver = Driver(TwoWindows().Group(1, "docs", 12));
        var tab = driver.ListWindows()[0].Tabs[1];

        Assert.False(driver.GroupTab(tab, "rig", isNew: true, homeHandle: 1).Grouped);
    }

    [Fact]
    public void Ungroups_every_member_of_the_named_group()
    {
        var driver = Driver(TwoWindows().Group(1, "mixed", 11, 12));
        var tab = driver.ListWindows()[0].Tabs[0];

        Assert.False(driver.Ungroup(tab, "other").Grouped);
        var outcome = driver.Ungroup(tab, "mixed");

        Assert.True(outcome.Grouped, outcome.Detail);
        Assert.All(driver.ListWindows()[0].Tabs, current => Assert.Null(current.Group));
    }

    [Fact]
    public void Moves_an_ungrouped_tab_to_another_window_of_its_profile()
    {
        var driver = Driver(TwoWindows());
        var tab = driver.ListWindows()[1].Tabs[0];

        var moved = driver.MoveToWindow(tab, 1);

        Assert.True(moved.Outcome.Grouped, moved.Outcome.Detail);
        Assert.Equal(1, moved.Tab?.WindowHandle);
        Assert.False(driver.MoveToWindow(driver.ListWindows()[0].Tabs[0], 99).Outcome.Grouped);
    }

    [Fact]
    public void Restores_each_windows_active_tab()
    {
        var driver = Driver(TwoWindows());
        var before = driver.ListWindows();
        var wanted = before[0].Tabs[1] with { Selected = true };
        var window = before[0] with { Tabs = [before[0].Tabs[0], wanted] };

        var restored = driver.RestoreSelection([window]);

        Assert.Equal(wanted.RuntimeId, restored[1]?.RuntimeId);
        Assert.True(driver.ListWindows()[0].Tabs[1].Selected);
    }

    [Fact]
    public void Nothing_is_brought_forward_and_there_are_no_saved_groups()
    {
        var driver = Driver(TwoWindows());

        using var lease = driver.Foreground(1);
        Assert.True(lease.Arrived);
        Assert.Empty(driver.SavedGroups(1, "Profile 2"));
        Assert.False(driver.DeleteSavedGroup(1, "Profile 2", "x").Deleted);
    }

    [Theory]
    [InlineData("/usr/lib/chromium/chromium", "chromium")]
    [InlineData("/opt/google/chrome/chrome", "google-chrome")]
    [InlineData("/opt/google/chrome-beta/chrome", "google-chrome-beta")]
    [InlineData("/usr/bin/python3", null)]
    [InlineData("", null)]
    public void The_host_names_the_browser_by_its_program(string program, string? folder) =>
        Assert.Equal(folder, new ExtensionConnection("s", "", "", program).ConfigFolder);

    [Fact]
    public void The_foreground_window_is_the_one_Chrome_reports_focused()
    {
        Assert.Equal(1, Driver(TwoWindows()).ForegroundWindow());
        Assert.Equal(0, Driver(new FakeExtension()).ForegroundWindow());
    }

    [Fact]
    public void Refuses_an_empty_group_title_and_a_tab_already_grouped()
    {
        var driver = Driver(TwoWindows().Group(1, "rig", 11));
        var tabs = driver.ListWindows()[0].Tabs;

        var untitled = driver.GroupTab(tabs[1], "", isNew: true, homeHandle: 1);
        var grouped = driver.GroupTab(tabs[0], "docs", isNew: true, homeHandle: 1);

        Assert.False(untitled.Grouped);
        Assert.Contains("empty", untitled.Detail, StringComparison.Ordinal);
        Assert.False(grouped.Grouped);
        Assert.Contains("already grouped", grouped.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_whose_windows_cannot_be_read_fails_the_read()
    {
        var driver = new ChromeExtension(new UnreadableBridge(), _ => Profiles, TimeProvider.System);

        var error = Assert.Throws<ChromeReadException>(driver.ListWindows);

        Assert.Contains("host went away", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Presence_is_always_long_away_and_unlocked()
    {
        var presence = new AwayPresence(TimeProvider.System);

        Assert.True(unchecked(presence.NowTick() - presence.LastInputTick()) >= uint.MaxValue / 2);
        Assert.False(presence.SessionLocked());
        Assert.Empty(presence.DevicesInUse());
    }

    [Fact]
    public void The_extension_errors_keep_their_message_and_cause()
    {
        var cause = new InvalidOperationException("cause");
        Exception[] errors =
        [
            new ExtensionCallException(), new ExtensionCallException("m"), new ExtensionCallException("m", cause),
            new ExtensionStepRefusedException(), new ExtensionStepRefusedException("m"), new ExtensionStepRefusedException("m", cause),
            new NativeFrameException(), new NativeFrameException("m"), new NativeFrameException("m", cause),
        ];

        Assert.All(errors.Where((_, i) => i % 3 > 0), error => Assert.Equal("m", error.Message));
        Assert.All(errors.Where((_, i) => i % 3 == 2), error => Assert.Same(cause, error.InnerException));
    }

    private sealed class UnreadableBridge : IExtensionBridge
    {
        public IReadOnlyList<ExtensionConnection> Connections() => [new("/run/rig/chrome-tabs/3.sock", "me@example.com", "0.1.0")];

        public System.Text.Json.Nodes.JsonNode? Send(ExtensionConnection connection, string command, System.Text.Json.Nodes.JsonObject? args = null) =>
            throw new ExtensionCallException("windows: the host went away");
    }
}
