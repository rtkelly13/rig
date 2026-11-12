using Rig.Core.Chrome;

namespace Rig.Core.Tests.Chrome;

/// <summary>
/// Chrome's tab model without a browser: accessible-name parsing, profiles, move-menu entries, screen order and the
/// saved-group plan. Replaces <c>test_chrome_tabs_titles</c>, <c>test_chrome_screen_order</c> and the parsing and
/// planning half of <c>test_chrome_saved_groups</c>.
/// </summary>
public sealed class ChromeModelTests
{
    [Theory]
    [InlineData("Inbox - Memory usage - 346 MB", "Inbox", true)]
    [InlineData("Inbox - High memory usage - 1,204 MB", "Inbox", true)]
    [InlineData("Docs - Memory usage - 67.1 MB", "Docs", true)]
    [InlineData("Discarded tab", "Discarded tab", false)]
    [InlineData("A - B - C", "A - B - C", false)]
    public void Memory_suffix_is_stripped_and_marks_the_tab_loaded(string name, string title, bool loaded)
    {
        var parsed = TabNames.Parse(name);

        Assert.Equal((title, loaded, false), (parsed.Title, parsed.Loaded, parsed.Audible));
    }

    [Theory]
    [InlineData("Page - Memory usage - 512 KB", 0.5)]
    [InlineData("Page - Memory usage - 2 GB", 2048.0)]
    [InlineData("Page - Memory usage - 1 TB", 1048576.0)]
    [InlineData("Page - Memory usage - 1048576 B", 1.0)]
    [InlineData("Page – Memory usage – 1,204MB  ", 1204.0)]
    public void Memory_is_read_in_megabytes(string name, double megabytes)
    {
        var parsed = TabNames.Parse(name);

        Assert.Equal("Page", parsed.Title);
        Assert.Equal(megabytes, parsed.MemoryMb);
    }

    [Fact]
    public void Alert_suffixes_are_stripped_and_live_media_counts_as_audible()
    {
        var parsed = TabNames.Parse("Google Meet - Camera or microphone recording - Audio playing - Memory usage - 340 MB");

        Assert.Equal("Google Meet", parsed.Title);
        Assert.Equal(["Camera or microphone recording", "Audio playing"], parsed.Alerts);
        Assert.True(parsed.Audible);
    }

    [Fact]
    public void En_dash_joined_call_is_audible()
    {
        var parsed = TabNames.Parse("Meet – Data & Platform Standup - Daily – Camera and microphone recording - Memory usage - 765 MB");

        Assert.Equal("Meet – Data & Platform Standup - Daily", parsed.Title);
        Assert.Equal(["Camera and microphone recording"], parsed.Alerts);
        Assert.True(parsed.Loaded);
        Assert.True(parsed.Audible);
        Assert.Contains("Camera and microphone recording", TabNames.LiveAlerts);
    }

    [Fact]
    public void Non_media_alerts_are_not_audible()
    {
        var parsed = TabNames.Parse("Page - Network error");

        Assert.Equal("Page", parsed.Title);
        Assert.Equal(["Network error"], parsed.Alerts);
        Assert.False(parsed.Audible);
    }

    [Theory]
    [InlineData("Docs - Pinned - Memory usage - 30 MB")]
    [InlineData("Docs – Pinned")]
    public void Pinned_label_is_stripped_and_recorded(string name)
    {
        var parsed = TabNames.Parse(name);

        Assert.Equal("Docs", parsed.Title);
        Assert.True(parsed.Pinned);
    }

    [Fact]
    public void Macos_memory_saver_label_is_stripped()
    {
        var parsed = TabNames.Parse("Docs - Inactive tab - 120 MB freed up");

        Assert.Equal(("Docs", false), (parsed.Title, parsed.Loaded));
    }

    [Theory]
    [InlineData("Example Domain – Part of unnamed group - Memory usage - 30.0 MB")]
    [InlineData("Example Domain – Part of group typed-title - Memory usage - 30.0 MB")]
    [InlineData("Example Domain - Part of group a - b - Memory usage - 30.0 MB")]
    public void Group_suffix_is_stripped(string name) => Assert.Equal("Example Domain", TabNames.Parse(name).Title);

    [Fact]
    public void Group_suffix_before_an_alert_keeps_the_alert()
    {
        var parsed = TabNames.Parse("Meet – Part of group calls - Audio playing - Memory usage - 30.0 MB");

        Assert.Equal("Meet", parsed.Title);
        Assert.Equal(["Audio playing"], parsed.Alerts);
    }

    [Theory]
    [InlineData(" group typed-title – 1 tab, •  Example Domain – Expanded", "typed-title")]
    [InlineData(" group data-platform – 12 tabs, •  A, •  B – Collapsed", "data-platform")]
    [InlineData(" unnamed group – 2 tabs, •  Example Domain – Expanded", "")]
    [InlineData("Example Domain", null)]
    public void Group_header_names_the_group(string name, string? title) => Assert.Equal(title, TabNames.ParseGroupHeader(name));

    [Fact]
    public void Profile_directory_comes_from_the_relaunch_command()
    {
        const string exe = """"C:\Program Files\Google\Chrome\Application\chrome.exe"""";

        Assert.Equal("Profile 4", WindowProfile.DirectoryFromRelaunchCommand($"""{exe} --profile-directory="Profile 4" """));
        Assert.Equal("Default", WindowProfile.DirectoryFromRelaunchCommand($"{exe} --profile-directory=Default"));
        Assert.Equal("Default", WindowProfile.DirectoryFromRelaunchCommand(exe));
        Assert.Null(WindowProfile.DirectoryFromRelaunchCommand(null));
    }

    [Fact]
    public void Profile_name_drops_the_chrome_suffix()
    {
        Assert.Equal("Ryan (work)", WindowProfile.NameFromRelaunchDisplayName("Ryan (work) - Chrome"));
        Assert.Equal("Home", WindowProfile.NameFromRelaunchDisplayName("Home - Google Chrome"));
        Assert.Null(WindowProfile.NameFromRelaunchDisplayName(" - Chrome"));
        Assert.Null(WindowProfile.NameFromRelaunchDisplayName(null));
    }

    [Theory]
    [InlineData("linking triage and 3 other tabs", "Linking Triage", 4, true)]
    [InlineData("linking triage and 1 other tab", "Linking Triage", 2, true)]
    [InlineData("linking triage", "Linking Triage", 1, true)]
    [InlineData("linking triage and 3 other tabs", "Linking Triage", 3, false)]
    [InlineData("linking triage and 3 other tabs", "Linking by Entity", 4, false)]
    [InlineData("some very long pull request ti… and 9 other tabs", "Some very long pull request title here", 10, true)]
    [InlineData("… and 9 other tabs", "Anything", 10, false)]
    [InlineData("new window", "New window", 3, false)]
    public void Move_menu_entries_name_a_window_by_active_tab_and_count(string entry, string active, int count, bool names) =>
        Assert.Equal(names, TabMenus.NamesWindow(entry, active, count));

    [Theory]
    [InlineData("add tab to new group", true)]
    [InlineData("add tab to group", true)]
    [InlineData("add tab to new split view", false)]
    public void Group_entries_are_told_from_split_view(string name, bool group) => Assert.Equal(group, TabMenus.IsGroupEntry(name));

    [Fact]
    public void Current_desktop_first_then_monitor_left_to_right_then_top_to_bottom()
    {
        ChromeWindow[] windows =
        [
            Window(1, desktop: 2),
            Window(2, monitor: new(2560, -1080), left: 2560, top: 0),
            Window(3, monitor: new(2560, -1080), left: 2560, top: -1080),
            Window(4, left: 900),
            Window(5, desktop: null),
            Window(6),
        ];

        Assert.Equal([6, 4, 3, 2, 1, 5], ChromeWindow.ScreenOrder(windows, currentDesktop: 1).Select(window => window.Handle));
    }

    [Fact]
    public void Windows_without_a_known_position_go_last_on_their_desktop()
    {
        ChromeWindow[] windows = [Window(1) with { Monitor = null, Rect = null }, Window(2)];

        Assert.Equal([2, 1], ChromeWindow.ScreenOrder(windows, currentDesktop: 1).Select(window => window.Handle));
    }

    [Fact]
    public void A_window_is_planned_in_its_scope_else_its_profile()
    {
        var window = Window(1);
        var tab = new ChromeTab(new([1, 2]), 1, 0, "a", "a", Selected: true, Pinned: false, Loaded: true, Audible: false);

        Assert.Equal("Profile 4", window.ProfileKey);
        Assert.Equal("private-1", (window with { ProfileScope = "private-1" }).ProfileKey);
        Assert.Equal("(unknown profile)", (window with { ProfileDir = null }).ProfileKey);
        Assert.Null(window.SelectedTab);
        Assert.Same(tab, (window with { Tabs = [tab with { Selected = false, Index = 1 }, tab] }).SelectedTab);
    }

    [Fact]
    public void Runtime_ids_compare_by_value()
    {
        var id = new RuntimeId([42, 7]);

        Assert.Equal(new RuntimeId([42, 7]), id);
        Assert.Equal(new RuntimeId([42, 7]).GetHashCode(), id.GetHashCode());
        Assert.NotEqual(new RuntimeId([42]), id);
        Assert.False(id.Equals(Missing()));
        Assert.False(id.Equals((object)"42.7"));
        Assert.Equal("42.7", id.ToString());
    }

    [Theory]
    [InlineData(" Google group – Opened", "Google", true)]
    [InlineData(" data-platform group – Closed", "data-platform", false)]
    [InlineData("Release group 2 group – Closed", "Release group 2", false)]
    public void Chip_names_read_title_and_state(string name, string title, bool opened) =>
        Assert.Equal(new ChipName(title, opened), SavedGroups.ParseChipName(name));

    [Theory]
    [InlineData("Tab groups")]
    [InlineData("Google")]
    [InlineData(" Google group - Opened")]
    [InlineData("")]
    public void Chip_names_ignore_anything_else_on_the_bar(string name) => Assert.Null(SavedGroups.ParseChipName(name));

    [Fact]
    public void The_union_of_bars_is_the_window_showing_the_most_chips()
    {
        var narrow = Bar("+Google", "-Claude");
        var wide = Bar("+Google", "-Claude", "-infrastructure");

        Assert.Same(wide, SavedGroups.UnionOfBars([narrow, wide, Bar("+Google", "-Claude", "-other")]));
        Assert.Empty(SavedGroups.UnionOfBars([]));
    }

    [Fact]
    public void Deletes_every_closed_copy_of_an_open_title()
    {
        var plan = SavedGroups.PlanConsolidation(Bar("+Google", "-Google", "-Google"));

        Assert.Equal([new SavedGroupDelete("Default", "Google", 2, 1, 2)], plan.Deletes);
        Assert.Equal(2, plan.Total);
    }

    [Fact]
    public void Keeps_one_closed_copy_when_none_is_open()
    {
        var plan = SavedGroups.PlanConsolidation(Bar("-YouTrack", "-YouTrack", "-YouTrack"));

        Assert.Equal([("YouTrack", 2)], plan.Deletes.Select(delete => (delete.Title, delete.Count)));
    }

    [Fact]
    public void Leaves_titles_with_a_single_copy()
    {
        var plan = SavedGroups.PlanConsolidation(Bar("+Google", "-playbook", "-shared-utilities"));

        Assert.Empty(plan.Deletes);
        Assert.Equal(0, plan.Total);
    }

    [Fact]
    public void Never_deletes_an_open_copy_and_reports_open_duplicates()
    {
        var plan = SavedGroups.PlanConsolidation(Bar("+Claude", "+Claude"));

        Assert.Empty(plan.Deletes);
        Assert.Equal([new OpenDuplicate("Default", "Claude", 2)], plan.OpenDuplicates);
    }

    [Fact]
    public void Titles_are_matched_per_profile_and_ordered_by_profile_then_position()
    {
        var plan = SavedGroups.PlanConsolidation([.. BarIn("Profile 4", "-Google"), .. Bar("+Google")]);

        Assert.Empty(plan.Deletes);
        Assert.Equal(["Default", "Profile 4"], plan.Groups.Select(group => group.Profile));
    }

    [Fact]
    public void The_live_session()
    {
        var plan = SavedGroups.PlanConsolidation(
            Bar("+Google", "+Claude", "+data-platform", "-Claude", "-data-platform", "-Google", "-YouTrack", "-playbook", "-shared-utilities"));

        Assert.Equal([("Google", 1), ("Claude", 1), ("data-platform", 1)], plan.Deletes.Select(delete => (delete.Title, delete.Count)));
    }

    [Fact]
    public void Macos_desktops_are_not_addressable()
    {
        var desktops = new NoChromeDesktops();
        desktops.SwitchTo(2);
        desktops.MoveWindow(1, 2);

        Assert.Null(desktops.Current());
        Assert.Null(desktops.DesktopOf(1));
        Assert.False(desktops.WindowExists(1));
    }

    [Fact]
    public void A_read_failure_carries_its_cause()
    {
        var cause = new InvalidOperationException("stale");

        Assert.Same(cause, new ChromeReadException("kept changing", cause).InnerException);
        Assert.Equal("kept changing", new ChromeReadException("kept changing").Message);
        Assert.NotNull(new ChromeReadException().Message);
    }

    private static ChromeWindow Window(long handle, int? desktop = 1, ScreenPoint? monitor = null, int left = 0, int top = 0) =>
        new(handle, "w", "Profile 4", null, handle, Minimized: false, desktop, [])
        {
            Rect = new(left, top, left + 800, top + 600),
            Monitor = monitor ?? new(0, 0),
        };

    private static RuntimeId? Missing() => null;

    // Chips as "+title" (open) or "-title" (closed), left to right.
    private static List<SavedGroup> Bar(params string[] chips) => BarIn("Default", chips);

    private static List<SavedGroup> BarIn(string profile, params string[] chips) =>
        [.. chips.Select((chip, position) => new SavedGroup(profile, chip[1..], chip[0] == '+', position))];
}
