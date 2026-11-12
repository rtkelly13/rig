using Rig.Core.Chrome;
using Rig.Core.Chrome.MacOS;

namespace Rig.Core.Tests.Chrome.MacOS;

/// <summary>Pairing accessibility evidence with the scripting snapshot, ported from <c>test_chrome_macos_reader.py</c>.</summary>
public sealed class MacReadTests
{
    private const string Notes = "Notes, \"雪\"\nline";
    private const string Address = "https://example.com/雪?q=a,b&x=\"c\"";

    private static readonly Dictionary<string, ProfileInfo> WorkProfile = new() { ["Profile 4"] = new ProfileInfo("Work") };

    private static readonly Dictionary<string, ProfileInfo> SharedAccount = new()
    {
        ["Default"] = new ProfileInfo("Personal", GaiaGivenName: "Ryan"),
        ["Profile 3"] = new ProfileInfo("Main", GaiaGivenName: "Ryan"),
    };

    private static JxaWindow Raw(string title = Notes, long id = 42, string windowTitle = "Chrome work", long tabId = 701, string mode = "normal") =>
        new(id, 1, windowTitle, 1, true, mode, [new JxaTab(tabId, title, Address)], new JxaBounds(10, 20, 800, 600));

    private static JxaSnapshot Snapshot(params JxaWindow[] windows) => new(123, true, windows);

    private static AxNode TabNode(string title = Notes, bool close = true, bool selected = true, long identity = 0) =>
        new(Identity: identity, Role: "AXRadioButton", Subrole: "AXTabButton", Title: title + " - Memory usage - 128 MB", Selected: selected,
            Children: close ? [new AxNode(Role: "AXButton", Title: "Close")] : []);

    private static AxNode AxWindow(string title = Notes, string name = "Work", bool close = true) =>
        new(Role: "AXWindow", Title: "Chrome work", Children:
        [
            new AxNode(Role: "AXButton", Identifier: "AvatarToolbarButton", Title: name),
            new AxNode(Role: "AXTabGroup", Children: [TabNode(title, close)]),
        ]);

    private static AxNode Slot(string title, bool selected = false) =>
        new(Role: "AXRadioButton", Subrole: "AXTabButton", Title: title, Selected: selected, Children: [new AxNode(Role: "AXButton", Title: "Close tab")]);

    private static AxNode Header(string title, int count, long identity = 901, bool? expanded = true) =>
        new(Role: "AXTabGroup", Identity: identity, Title: $" {(title.Length > 0 ? $"group {title}" : "unnamed group")} – {count} tabs", Expanded: expanded);

    private static AxNode Strip(params AxNode[] children) => new(Role: "AXTabGroup", Children: children);

    private static ChromeWindow One(JxaSnapshot snapshot, AxNode[] nodes, IReadOnlyDictionary<string, ProfileInfo>? profiles = null) =>
        Assert.Single(MacRead.Windows(snapshot, nodes, profiles ?? WorkProfile));

    [Fact]
    public void Unicode_url_and_native_metadata()
    {
        var window = One(Snapshot(Raw()), [AxWindow()]);
        var tab = Assert.Single(window.Tabs);
        Assert.Equal(42, window.Handle);
        Assert.True(window.Minimized);
        Assert.Equal(new WindowRect(10, 20, 810, 620), window.Rect);
        Assert.Equal("Profile 4", window.ProfileDir);
        Assert.Equal("Work", window.ProfileName);
        Assert.Equal("Profile 4", window.ProfileKey);
        Assert.Null(window.ProfileScope);
        Assert.Equal(Notes, tab.Title);
        Assert.Equal(new RuntimeId([701]), tab.RuntimeId);
        Assert.Equal(Address, tab.Url!.OriginalString);
        Assert.True(tab.Loaded);
        Assert.Equal(128, tab.MemoryMb);
        Assert.False(tab.Audible);
        Assert.False(tab.Pinned);
        Assert.True(tab.Selected);
        Assert.Empty(tab.Alerts);
    }

    [Fact]
    public void An_elided_scripting_title_matches_the_full_native_title()
    {
        var node = AxWindow("A long pull request title") with { Title = "A long pull request title - Google Chrome – Work" };
        var window = One(Snapshot(Raw("A long pull request title", windowTitle: "A long…title")), [node]);
        Assert.Equal("Profile 4", window.ProfileDir);
        Assert.False(window.Tabs[0].Pinned);
    }

    [Fact]
    public void The_full_title_alone_matches_too()
    {
        var node = AxWindow("Page") with { Title = "Page - Google Chrome" };
        Assert.Same(node, MacRead.WindowNode([node], Raw("Page", windowTitle: "P…"), [Raw("Page", windowTitle: "P…")]));
    }

    [Fact]
    public void An_untitled_native_window_matches_by_tab_strip()
    {
        var window = One(Snapshot(Raw()), [AxWindow() with { Title = "" }]);
        Assert.Equal("Profile 4", window.ProfileDir);
        Assert.False(window.Tabs[0].Pinned);
    }

    [Fact]
    public void Identical_untitled_strips_are_ambiguous()
    {
        var node = AxWindow() with { Title = "" };
        var window = One(Snapshot(Raw()), [node, node with { Identity = 2 }]);
        Assert.Null(window.ProfileDir);
        Assert.True(window.Tabs[0].Pinned);
    }

    [Fact]
    public void A_title_two_scripting_windows_share_is_ambiguous()
    {
        var node = AxWindow();
        var first = Raw();
        Assert.Null(MacRead.WindowNode([node], first, [first, Raw(id: 43, tabId: 702)]));
    }

    [Fact]
    public void No_window_matches_neither_title_nor_strip()
    {
        var node = AxWindow("Other") with { Title = "Unrelated" };
        Assert.Null(MacRead.WindowNode([node], Raw(), [Raw()]));
    }

    [Fact]
    public void An_active_index_past_the_tabs_matches_only_by_exact_title()
    {
        var raw = Raw() with { Active = 2 };
        Assert.False(MacRead.MatchesWindowTitle(AxWindow() with { Title = Notes + " - Google Chrome" }, raw));
        Assert.True(MacRead.MatchesWindowTitle(AxWindow(), raw));
    }

    [Fact]
    public void Repeated_graph_references_do_not_duplicate_tabs()
    {
        var tab = TabNode(identity: 701);
        var node = new AxNode(Role: "AXWindow", Children: [new AxNode(Children: [tab]), new AxNode(Children: [tab])]);
        Assert.Equal([new TabSlot(tab, null)], MacRead.TabSlots(node, 42));
    }

    [Fact]
    public void The_same_element_decoded_twice_collapses_by_value()
    {
        var node = new AxNode(Role: "AXWindow", Children: [TabNode(identity: 701), TabNode(identity: 701)]);
        Assert.Single(MacRead.TabSlots(node, 42));
    }

    [Fact]
    public void Conflicting_repeated_identity_refuses_metadata()
    {
        var tab = TabNode(identity: 701);
        var node = new AxNode(Role: "AXWindow", Children: [tab, tab with { Selected = false }]);
        Assert.Empty(MacRead.TabSlots(node, 42));
    }

    [Fact]
    public void Tabs_without_identity_are_kept_apart()
    {
        var node = new AxNode(Role: "AXWindow", Children: [TabNode(), TabNode()]);
        Assert.Equal(2, MacRead.TabSlots(node, 42).Count);
    }

    [Fact]
    public void A_tab_described_as_tab_counts_without_its_subrole()
    {
        var tab = new AxNode(Role: "AXRadioButton", Description: "tab");
        Assert.Equal([new TabSlot(tab, null)], MacRead.TabSlots(new AxNode(Children: [tab]), 42));
    }

    [Fact]
    public void Without_accessibility_tabs_are_protected()
    {
        var tab = One(Snapshot(Raw()), []).Tabs[0];
        Assert.True(tab.Pinned);
        Assert.True(tab.Audible);
        Assert.Equal([MacRead.MetadataUnavailable], tab.Alerts);
        Assert.False(tab.Loaded);
        Assert.Null(tab.MemoryMb);
        Assert.Equal(Notes, tab.Name);
    }

    [Fact]
    public void A_page_title_does_not_supply_safety_metadata()
    {
        const string title = "Page - Audio playing - Memory usage - 500 MB";
        var tab = One(Snapshot(Raw(title)), []).Tabs[0];
        Assert.Equal(title, tab.Title);
        Assert.Equal(title, tab.Name);
        Assert.False(tab.Loaded);
        Assert.Null(tab.MemoryMb);
    }

    [Fact]
    public void Audio_and_group_come_from_accessibility()
    {
        var tab = new AxNode(Role: "AXRadioButton", Subrole: "AXTabButton", Selected: true,
            Title: "Notes - Audio playing - Part of group Old - Memory usage - 2 GB",
            Children: [new AxNode(Role: "AXButton", Title: "Close tab")]);
        var group = Strip(new AxNode(Role: "AXTabGroup", Identity: 901, Title: " group Current – 1 tab – Expanded"), tab);
        var result = One(Snapshot(Raw("Notes")), [AxWindow("Notes") with { Children = [group] }]).Tabs[0];
        Assert.True(result.Audible);
        Assert.Equal(2048, result.MemoryMb);
        Assert.Equal(new TabGroup(new RuntimeId([0, 42, 0, 901, -1]), "Current"), result.Group);
    }

    [Fact]
    public void A_hidden_close_control_does_not_mean_pinned()
    {
        var tab = One(Snapshot(Raw()), [AxWindow(close: false)]).Tabs[0];
        Assert.False(tab.Pinned);
        Assert.False(tab.Audible);
    }

    [Fact]
    public void A_pinned_label_marks_the_tab_pinned()
    {
        var pinned = TabNode(close: false) with { Title = Notes + " – Pinned - Memory usage - 128 MB" };
        var node = AxWindow() with { Children = [AxWindow().Children[0], Strip(pinned)] };
        var tab = One(Snapshot(Raw()), [node]).Tabs[0];
        Assert.True(tab.Pinned);
        Assert.False(tab.Audible);
        Assert.Empty(tab.Alerts);
    }

    [Fact]
    public void A_pinned_label_with_a_close_control_is_inconsistent() =>
        Assert.False(MacRead.PinnedLabelsConsistent([TabNode() with { Title = "A – Pinned" }]));

    [Fact]
    public void A_pinned_label_after_an_unpinned_tab_is_inconsistent()
    {
        var plain = new AxNode(Role: "AXRadioButton", Subrole: "AXTabButton", Title: "A");
        var pinned = plain with { Title = "B – Pinned" };
        Assert.True(MacRead.PinnedLabelsConsistent([pinned, plain]));
        Assert.False(MacRead.PinnedLabelsConsistent([plain, pinned]));
    }

    [Fact]
    public void Inconsistent_pinned_labels_drop_the_strip()
    {
        var node = AxWindow() with { Children = [Strip(TabNode() with { Title = Notes + " – Pinned" })] };
        Assert.Null(MacRead.AlignedSlots(node, Raw()));
    }

    [Fact]
    public void Profiles_are_never_guessed_and_unknown_windows_are_isolated()
    {
        var windows = MacRead.Windows(Snapshot(Raw(), Raw("Other", id: 43, tabId: 702)), [], WorkProfile);
        Assert.All(windows, window => Assert.Null(window.ProfileDir));
        Assert.Equal("(unknown profile: window 42)", windows[0].ProfileKey);
        Assert.NotEqual(windows[0].ProfileKey, windows[1].ProfileKey);
    }

    [Fact]
    public void A_bare_profile_name_needs_a_corroborated_control()
    {
        var node = AxWindow() with { Children = [new AxNode(Role: "AXButton", Title: "Work")] };
        Assert.Null(One(Snapshot(Raw()), [node]).ProfileDir);
    }

    [Fact]
    public void A_bare_name_outside_the_toolbar_avatar_does_not_count()
    {
        // The toolbar names its avatar; a button elsewhere with a profile's bare name is not it.
        var toolbar = new AxNode(Role: "AXToolbar", Children:
        [
            new AxNode(Role: "AXButton", Title: "Home"),
            new AxNode(Role: "AXPopUpButton", Title: "Chrome"),
        ]);
        var elsewhere = new AxNode(Role: "AXButton", Title: "Work");
        var node = AxWindow() with { Children = [toolbar, elsewhere, AxWindow().Children[1]] };
        Assert.Null(MacRead.ProfileOf(node, WorkProfile));
    }

    [Fact]
    public void An_explicit_profile_label_needs_no_avatar()
    {
        var node = new AxNode(Children: [new AxNode(Role: "AXPopUpButton", Help: "Profile: Work")]);
        Assert.Equal(new ProfileMatch("Profile 4", "Work"), MacRead.ProfileOf(node, WorkProfile));
    }

    [Fact]
    public void Profiles_without_a_name_are_ignored()
    {
        var node = AxWindow(name: "Work");
        var profiles = new Dictionary<string, ProfileInfo> { ["Profile 9"] = new ProfileInfo(GaiaName: "Work"), ["Profile 4"] = new ProfileInfo("Work") };
        Assert.Equal(new ProfileMatch("Profile 4", "Work"), MacRead.ProfileOf(node, profiles));
    }

    [Fact]
    public void No_node_has_no_profile() => Assert.Null(MacRead.ProfileOf(null, WorkProfile));

    [Fact]
    public void Several_profile_windows_map_from_the_ui()
    {
        var first = Raw("First");
        var second = Raw("Second", id: 43, windowTitle: "Home window", tabId: 702);
        AxNode[] nodes = [AxWindow("First"), AxWindow("Second", name: "Home") with { Title = "Home window" }];
        var profiles = new Dictionary<string, ProfileInfo> { ["Profile 4"] = new ProfileInfo("Work"), ["Default"] = new ProfileInfo("Home") };
        Assert.Equal(["Profile 4", "Default"], MacRead.Windows(Snapshot(first, second), nodes, profiles).Select(window => window.ProfileKey));
    }

    [Fact]
    public void Duplicate_profile_names_are_ambiguous()
    {
        var profiles = new Dictionary<string, ProfileInfo> { ["Default"] = new ProfileInfo("Work"), ["Profile 4"] = new ProfileInfo("Work") };
        Assert.Null(One(Snapshot(Raw()), [AxWindow()], profiles).ProfileDir);
    }

    [Fact]
    public void Private_windows_do_not_share_the_normal_profile_scope()
    {
        var window = One(Snapshot(Raw(mode: "incognito")), [AxWindow()]);
        Assert.Equal("Profile 4", window.ProfileDir);
        Assert.NotEqual("Profile 4", window.ProfileKey);
    }

    [Fact]
    public void A_mismatched_selection_protects_the_tab()
    {
        var node = AxWindow() with { Children = [Strip(TabNode(selected: false))] };
        Assert.True(One(Snapshot(Raw()), [node]).Tabs[0].Pinned);
    }

    [Fact]
    public void Web_content_cannot_impersonate_the_profile_or_tabs()
    {
        var node = new AxNode(Role: "AXWindow", Title: "Chrome work", Children: [new AxNode(Role: "AXWebArea", Children: AxWindow().Children)]);
        var window = One(Snapshot(Raw()), [node]);
        Assert.Null(window.ProfileDir);
        Assert.True(window.Tabs[0].Pinned);
    }

    [Fact]
    public void The_walk_never_enters_a_skipped_role()
    {
        var hidden = new AxNode(Title: "inside");
        var node = new AxNode(Title: "root", Children: [new AxNode(Role: "AXMenuBar", Children: [hidden]), new AxNode(Title: "after")]);
        Assert.Equal(["root", "", "after"], MacRead.Walk(node).Select(item => item.Title));
    }

    [Fact]
    public void No_windows_reads_as_none() => Assert.Empty(MacRead.Windows(Snapshot(), [], WorkProfile));

    [Fact]
    public void Toolbar_profile_without_an_identifier()
    {
        var toolbar = new AxNode(Role: "AXToolbar", Children:
        [
            new AxNode(Role: "AXButton", Title: "Work"),
            new AxNode(Role: "AXPopUpButton", Title: "Customize and control Google Chrome"),
        ]);
        var node = AxWindow() with { Children = [toolbar, AxWindow().Children[1]] };
        Assert.Equal("Profile 4", One(Snapshot(Raw()), [node]).ProfileDir);
    }

    [Fact]
    public void A_signed_in_avatar_label_names_the_profile()
    {
        var toolbar = new AxNode(Role: "AXToolbar", Children:
        [
            new AxNode(Role: "AXButton", Title: "Ryan (Main)"),
            new AxNode(Role: "AXPopUpButton", Title: "Chrome"),
        ]);
        var node = AxWindow() with { Children = [toolbar, AxWindow().Children[1]] };
        Assert.Equal("Profile 3", One(Snapshot(Raw()), [node], SharedAccount).ProfileDir);
    }

    [Fact]
    public void A_shared_account_name_alone_is_ambiguous()
    {
        var node = AxWindow(name: "Ryan");
        Assert.Null(One(Snapshot(Raw()), [node], SharedAccount).ProfileDir);
    }

    [Fact]
    public void Stable_duplicate_titles_use_the_verified_order()
    {
        var raw = Raw("Duplicate") with { Tabs = [new JxaTab(701, "Duplicate", Address), new JxaTab(702, "Duplicate", "https://other.example/")] };
        var node = AxWindow("Duplicate") with { Children = [AxWindow().Children[0], Strip(TabNode("Duplicate"), TabNode("Duplicate", selected: false))] };
        var tabs = One(Snapshot(raw), [node]).Tabs;
        Assert.All(tabs, tab => Assert.False(tab.Pinned));
        Assert.Equal([new RuntimeId([701]), new RuntimeId([702])], tabs.Select(tab => tab.RuntimeId));
        Assert.Equal([true, false], tabs.Select(tab => tab.Selected));
        Assert.Equal([0, 1], tabs.Select(tab => tab.Index));
    }

    [Fact]
    public void A_group_suffix_without_a_header_protects_the_tab()
    {
        var tab = TabNode("Notes") with { Title = "Notes - Part of group Stale - Memory usage - 128 MB" };
        var node = AxWindow("Notes") with { Children = [AxWindow().Children[0], Strip(tab)] };
        var result = One(Snapshot(Raw("Notes")), [node]).Tabs[0];
        Assert.Null(result.Group);
        Assert.True(result.Pinned);
    }

    [Fact]
    public void A_sibling_group_count_does_not_capture_loose_tabs()
    {
        var result = MacRead.TabSlots(Strip(Header("Work", 1), Slot("Grouped - Part of group Old"), Slot("Ungrouped")), 42);
        Assert.Equal(["Grouped - Part of group Old", "Ungrouped"], result.Select(slot => slot.Node.Title));
        Assert.Equal(new TabGroup(new RuntimeId([0, 42, 0, 901, -1]), "Work"), result[0].Group);
        Assert.Null(result[1].Group);
    }

    [Fact]
    public void Adjacent_sibling_groups_and_loose_tabs()
    {
        var result = MacRead.TabSlots(
            Strip(Slot("Before"), Header("Work", 2), Slot("One"), Slot("Two"), Header("", 1, 902), Slot("Three"), Slot("After")), 42);
        Assert.Equal([null, "Work", "Work", "", null], result.Select(slot => slot.Group?.Title));
        Assert.Equal(result[1].Group, result[2].Group);
        Assert.NotEqual(result[2].Group, result[3].Group);
    }

    public static TheoryData<AxNode[]> Ambiguous => new()
    {
        { [Header("Work", 1, expanded: false), Slot("Loose after hidden group")] },
        { [Header("Work", 1, expanded: null), Slot("Unknown expansion")] },
        { [Header("Work", 2), Slot("Only visible member")] },
        { [Header("Work", 2), Slot("One"), Header("Next", 1, 902), Slot("Two")] },
        { [Header("Work", 0), Slot("Loose")] },
        { [Header("Work", 1) with { Title = " group Work – 1 tab – Collapsed" }, Slot("One")] },
        { [Header("Work", 1, expanded: null) with { Title = " group Work – 1 tab – Expanded – Collapsed" }, Slot("One")] },
        { [new AxNode(Role: "AXTabGroup", Identity: 901, Title: " group Work – 2 tabs – 1 tab – Expanded", Expanded: true), Slot("Grouped"), Slot("Loose")] },
        { [new AxNode(Role: "AXTabGroup", Identity: 901, Title: " group Work – 1 tab, • Preview – 2 tabs – Expanded", Expanded: true), Slot("Grouped"), Slot("Loose")] },
        { [new AxNode(Role: "AXTabGroup", Identity: 901, Title: " group Work – 99999999999 tabs", Expanded: true), Slot("One")] },
        { [new AxNode(Role: "AXTabGroup", Identity: 901, Title: " group Work – ٢ tabs", Expanded: true), Slot("One"), Slot("Two")] },
        { [new AxNode(Role: "AXButton", Title: " group Work – 1 tab", Expanded: true), Slot("One")] },
        { [Header("Work", 1), new AxNode(Role: "AXButton", Title: "Not a tab")] },
    };

    [Theory]
    [MemberData(nameof(Ambiguous))]
    public void Ambiguous_or_collapsed_groups_refuse_membership(AxNode[] children) =>
        Assert.Empty(MacRead.TabSlots(Strip(children), 42));

    [Fact]
    public void A_collapsed_header_protects_even_a_visible_ungrouped_tab()
    {
        var node = new AxNode(Role: "AXWindow", Title: "Chrome work", Children: [Strip(Header("Hidden", 1, expanded: false), Slot("Visible", selected: true))]);
        var tab = One(Snapshot(Raw("Visible")), [node]).Tabs[0];
        Assert.True(tab.Pinned);
        Assert.True(tab.Audible);
        Assert.Null(tab.Group);
    }

    [Fact]
    public void An_unproven_nested_wrapper_assigns_no_membership() =>
        Assert.Empty(MacRead.TabSlots(new AxNode(Role: "AXGroup", Children: [Header("Work", 1), Slot("One")]), 42));

    [Fact]
    public void A_failure_below_fails_the_whole_strip() =>
        Assert.Empty(MacRead.TabSlots(new AxNode(Children: [Strip(Header("Work", 2), Slot("One")), Slot("Loose")]), 42));

    [Fact]
    public void Duplicate_window_ids_throw() =>
        Assert.Equal("Duplicate Chrome window ID", Assert.Throws<ChromeMacException>(() => MacRead.Windows(Snapshot(Raw(), Raw(tabId: 702)), [], WorkProfile)).Message);

    [Fact]
    public void Duplicate_tab_ids_throw() =>
        Assert.Equal("Duplicate Chrome tab ID", Assert.Throws<ChromeMacException>(() => MacRead.Windows(Snapshot(Raw(), Raw(id: 43)), [], WorkProfile)).Message);

    [Fact]
    public void An_active_tab_past_the_strip_throws() =>
        Assert.Equal("Invalid Chrome window state", Assert.Throws<ChromeMacException>(() => MacRead.Windows(Snapshot(Raw() with { Active = 2 }), [], WorkProfile)).Message);

    [Fact]
    public void A_tab_id_past_int_range_throws() =>
        Assert.Equal("Invalid Chrome tab identity", Assert.Throws<ChromeMacException>(() => MacRead.Tab(new JxaTab(1L + int.MaxValue, "T", Address), 0, Raw(), null)).Message);

    [Fact]
    public void Without_bounds_there_is_no_rect() => Assert.Null(One(Snapshot(Raw() with { Bounds = null }), []).Rect);

    [Theory]
    [InlineData(int.MaxValue, 0, 1, 1)]
    [InlineData(0, int.MaxValue, 1, 1)]
    [InlineData(int.MinValue, 0, -1, 1)]
    [InlineData(0, int.MinValue, 1, -1)]
    public void Edges_past_a_pixel_coordinate_have_no_rect(int x, int y, int width, int height) =>
        Assert.Null(One(Snapshot(Raw() with { Bounds = new JxaBounds(x, y, width, height) }), []).Rect);

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("relative/path")]
    public void An_address_that_is_not_absolute_has_no_url(string address) =>
        Assert.Null(MacRead.Tab(new JxaTab(701, Notes, address), 0, Raw(), null).Url);

    [Theory]
    [InlineData("about:blank")]
    [InlineData("chrome://newtab/")]
    [InlineData("https://example.com/a b")]
    [InlineData("https://例え.jp/パス?q=雪")]
    [InlineData("https://example.com/%7Euser")]
    public void An_absolute_address_keeps_its_exact_text(string address) =>
        Assert.Equal(address, MacRead.Tab(new JxaTab(701, Notes, address), 0, Raw(), null).Url!.OriginalString);

    [Fact]
    public void A_lone_surrogate_address_keeps_its_text_or_has_no_url()
    {
        const string address = "https://example.com/\ud800";
        var url = MacRead.Tab(new JxaTab(701, Notes, address), 0, Raw(), null).Url;
        Assert.True(url is null || url.OriginalString == address);
    }

    [Fact]
    public void A_title_that_disagrees_with_its_slot_is_protected()
    {
        var tab = MacRead.Tab(new JxaTab(701, "Renamed", Address), 0, Raw(), new TabSlot(TabNode(), null));
        Assert.True(tab.Pinned);
        Assert.Equal([MacRead.MetadataUnavailable], tab.Alerts);
        Assert.Equal(128, tab.MemoryMb);
        Assert.Equal(TabNode().Name, tab.Name);
    }

    [Theory]
    [InlineData(42L, 901L, new[] { 0, 42, 0, 901, -1 })]
    [InlineData(0x1_0000_0002L, -1L, new[] { 1, 2, -1, -1, -1 })]
    [InlineData(long.MaxValue, long.MinValue, new[] { int.MaxValue, -1, int.MinValue, 0, -1 })]
    public void Group_ids_keep_both_halves(long window, long header, int[] expected) =>
        Assert.Equal(new RuntimeId(expected), MacRead.GroupId(window, header));

    [Fact]
    public void Nodes_compare_by_value_with_their_children()
    {
        var one = new AxNode(Role: "AXWindow", Children: [new AxNode(Title: "a")]);
        var two = new AxNode(Role: "AXWindow", Children: [new AxNode(Title: "a")]);
        Assert.Equal(one, two);
        Assert.Equal(one.GetHashCode(), two.GetHashCode());
        Assert.NotEqual(one, two with { Children = [new AxNode(Title: "b")] });
        Assert.NotEqual(one, two with { Subrole = "x" });
        Assert.NotEqual(one, two with { Identifier = "x" });
        Assert.NotEqual(one, two with { Help = "x" });
        Assert.NotEqual(one, two with { Expanded = true });
        Assert.False(one.Equals((object)"not a node"));
        Assert.Empty(new AxNode().Children);
        Assert.Equal("desc", new AxNode(Description: "desc").Name);
    }
}
