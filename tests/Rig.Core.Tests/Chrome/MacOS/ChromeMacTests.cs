using Rig.Core.Chrome;
using Rig.Core.Chrome.MacOS;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Chrome.MacOS;

/// <summary>
/// The macOS driver over its two seams: Chrome's scripting replies through a fake <see cref="IProc"/>, and its native
/// accessibility tree through <see cref="FakeAxApi"/>. No real Chrome, permission prompt or input is touched.
/// </summary>
public sealed class ChromeMacTests
{
    private const long WindowId = 42;
    private const long TabId = 701;
    private const string Title = "Notes";
    private const string Address = "https://example.com/notes";

    private static readonly Dictionary<string, ProfileInfo> Profiles = new() { ["Profile 4"] = new ProfileInfo("Work") };

    private readonly FakeAxApi _api = new();
    private readonly FakeAxApi.Element _tab;
    private readonly FakeAxApi.Element _strip;
    private readonly FakeAxApi.Element _window;

    public ChromeMacTests()
    {
        var close = new FakeAxApi.Element(13, new()
        {
            ["AXRole"] = "AXButton",
            ["AXTitle"] = "Close",
        });
        _tab = new FakeAxApi.Element(12, new()
        {
            ["AXRole"] = "AXRadioButton",
            ["AXSubrole"] = "AXTabButton",
            ["AXTitle"] = $"{Title} - Memory usage - 128 MB",
            ["AXValue"] = true,
            ["AXChildren"] = new[] { close },
        });
        _strip = new FakeAxApi.Element(11, new()
        {
            ["AXRole"] = "AXTabGroup",
            ["AXChildren"] = new[] { _tab },
        });
        var avatar = new FakeAxApi.Element(10, new()
        {
            ["AXRole"] = "AXButton",
            ["AXIdentifier"] = "AvatarToolbarButton",
            ["AXTitle"] = "Work",
        });
        _window = new FakeAxApi.Element(1, new()
        {
            ["AXRole"] = "AXWindow",
            ["AXTitle"] = "Chrome work",
            ["AXChildren"] = new[] { avatar, _strip },
        });
        _api.App["AXRole"] = "AXApplication";
        _api.App["AXFrontmost"] = true;
        _api.App["AXWindows"] = new[] { _window };
        _api.App["AXChildren"] = new[] { _window };
    }

    private static JxaWindow RawWindow() =>
        new(WindowId, 1, "Chrome work", 1, false, "normal", [new JxaTab(TabId, Title, Address)], new JxaBounds(10, 20, 800, 600));

    private static JxaSnapshot Snapshot(params JxaWindow[] windows) => new(123, true, windows);

    // The exact keys Chrome's scripting dictionary writes; the records deliberately keep .NET names.
    private static string Json(JxaSnapshot snapshot) =>
        new System.Text.Json.Nodes.JsonObject
        {
            ["pid"] = snapshot.Pid,
            ["frontmost"] = snapshot.Frontmost,
            ["windows"] = new System.Text.Json.Nodes.JsonArray(
                [.. snapshot.Windows.Select(window => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = window.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["index"] = window.Index,
                    ["title"] = window.Title,
                    ["active"] = window.Active,
                    ["minimized"] = window.Minimized,
                    ["mode"] = window.Mode,
                    ["bounds"] = window.Bounds is { } bounds
                        ? new System.Text.Json.Nodes.JsonObject
                        {
                            ["x"] = bounds.X,
                            ["y"] = bounds.Y,
                            ["width"] = bounds.Width,
                            ["height"] = bounds.Height,
                        }
                        : null,
                    ["tabs"] = new System.Text.Json.Nodes.JsonArray(
                        [.. window.Tabs.Select(tab => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = tab.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["title"] = tab.Title,
                            ["url"] = tab.Address,
                        })]),
                })]),
        }.ToJsonString();

    private static (ChromeMac Mac, FakeProc Proc) Over(FakeAxApi api, JxaSnapshot snapshot)
    {
        var proc = new FakeProc().Answer(new Ran(0, Json(snapshot), string.Empty), "/usr/bin/osascript", "-l", "JavaScript", "-");
        return (new ChromeMac(new ChromeJxa(proc), () => api, () => Profiles, new AdvancingTime(), _ => { }), proc);
    }

    private ChromeMac Over(IChromeJxa jxa) =>
        new(jxa, () => _api, () => Profiles, new AdvancingTime(), _ => { });

    [Fact]
    public void List_windows_pairs_scripting_identity_with_native_metadata()
    {
        var (mac, proc) = Over(_api, Snapshot(RawWindow()));

        var window = Assert.Single(mac.ListWindows());

        Assert.Equal(WindowId, window.Handle);
        Assert.Equal("Profile 4", window.ProfileDir);
        Assert.Equal("Work", window.ProfileName);
        var tab = Assert.Single(window.Tabs);
        Assert.Equal(new RuntimeId([(int)TabId]), tab.RuntimeId);
        Assert.Equal(Title, tab.Title);
        Assert.Equal(Address, tab.Url!.OriginalString);
        Assert.True(tab.Selected);
        Assert.False(tab.Pinned);
        Assert.True(tab.Loaded);
        Assert.Equal(128, tab.MemoryMb);
        Assert.Equal(2, proc.Requests.Count);
        Assert.All(proc.Requests, request => Assert.Equal(JxaScripts.Snapshot, request.StdinText));
    }

    [Fact]
    public void List_windows_is_empty_when_Chrome_is_not_running()
    {
        var (mac, _) = Over(_api, Snapshot());

        Assert.Empty(mac.ListWindows());
    }

    [Fact]
    public void Foreground_window_is_zero_when_Chrome_is_not_frontmost()
    {
        var (mac, _) = Over(_api, new JxaSnapshot(123, false, [RawWindow()]));

        Assert.Equal(0, mac.ForegroundWindow());
    }

    [Fact]
    public void Foreground_window_is_the_front_window_when_Chrome_is_active()
    {
        var (mac, _) = Over(_api, Snapshot(RawWindow()));

        Assert.Equal(WindowId, mac.ForegroundWindow());
    }

    [Fact]
    public void Foreground_window_refuses_an_ambiguous_front()
    {
        var (mac, _) = Over(_api, Snapshot(RawWindow(), RawWindow() with { Id = 43 }));

        var error = Assert.Throws<ChromeMacException>(() => mac.ForegroundWindow());
        Assert.Equal("Cannot identify Chrome's foreground window", error.Message);
    }

    [Fact]
    public void Close_tab_refuses_a_tab_whose_URL_is_unavailable()
    {
        var tab = Tab() with { Url = null };

        var outcome = Over(new ScriptedJxa()).CloseTab(tab);

        Assert.False(outcome.Closed);
        Assert.Equal("URL unavailable; refusing close", outcome.Detail);
    }

    [Fact]
    public void Close_tab_refuses_a_pinned_tab_before_asking_Chrome()
    {
        _tab.Attributes["AXTitle"] = $"{Title} - Pinned";
        _tab.Attributes["AXChildren"] = Array.Empty<FakeAxApi.Element>();
        var jxa = new ScriptedJxa()
            .Snapshot(Snapshot(RawWindow()))
            .Snapshot(Snapshot(RawWindow()));
        var outcome = Over(jxa).CloseTab(Tab());

        Assert.False(outcome.Closed);
        Assert.Equal("tab is pinned or audible; refusing close", outcome.Detail);
        Assert.Empty(jxa.Scripts);
    }

    [Fact]
    public void Close_tab_invokes_the_close_only_while_the_title_and_URL_match()
    {
        var jxa = new ScriptedJxa()
            .Snapshot(Snapshot(RawWindow()))
            .Snapshot(Snapshot(RawWindow()))
            .Reply("invoked")
            .Snapshot(Snapshot());
        var mac = Over(jxa);

        var outcome = mac.CloseTab(Tab());

        Assert.True(outcome.Closed);
        Assert.Equal("closed", outcome.Detail);
        Assert.Contains("target.close()", jxa.Scripts.Single(), StringComparison.Ordinal);
        Assert.Contains(Address, jxa.Scripts.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Close_tab_reports_a_close_Chrome_refused()
    {
        var jxa = new ScriptedJxa()
            .Snapshot(Snapshot(RawWindow()))
            .Snapshot(Snapshot(RawWindow()))
            .Reply("stale");

        var outcome = Over(jxa).CloseTab(Tab());

        Assert.False(outcome.Closed);
        Assert.Equal("close refused: stale", outcome.Detail);
    }

    [Fact]
    public void Close_tab_reports_a_tab_that_stayed_after_the_close()
    {
        var jxa = new ScriptedJxa()
            .Snapshot(Snapshot(RawWindow()))
            .Snapshot(Snapshot(RawWindow()))
            .Reply("invoked")
            .Snapshot(Snapshot(RawWindow()))
            .Snapshot(Snapshot(RawWindow()))
            .Snapshot(Snapshot(RawWindow()));

        var outcome = Over(jxa).CloseTab(Tab());

        Assert.False(outcome.Closed);
        Assert.Equal("invoked close but the tab is still present; a prompt may be open", outcome.Detail);
    }

    [Fact]
    public void Restore_selection_uses_the_original_tab_and_then_the_fallback()
    {
        var original = Tab();
        var fallback = Tab() with { RuntimeId = new RuntimeId([702]) };
        var window = ChromeWindowWith(original, fallback);
        var jxa = new ScriptedJxa().Reply("missing").Reply("selected");

        var restored = Over(jxa).RestoreSelection([window], new Dictionary<long, ChromeTab> { [WindowId] = fallback });

        Assert.Equal(fallback, restored[WindowId]);
        Assert.Equal(2, jxa.Scripts.Count);
    }

    [Fact]
    public void Restore_selection_reports_no_tab_when_the_script_fails()
    {
        var jxa = new ScriptedJxa { FailAct = true };

        var restored = Over(jxa).RestoreSelection([ChromeWindowWith(Tab())]);

        Assert.Null(restored[WindowId]);
    }

    [Fact]
    public void Group_tab_refuses_a_tab_that_is_already_grouped()
    {
        var tab = Tab() with { Group = new TabGroup(new RuntimeId([1, 2, 3, 4, -1]), "Work") };

        var outcome = Over(new ScriptedJxa()).GroupTab(tab, "Work", isNew: false);

        Assert.False(outcome.Grouped);
        Assert.Equal("Tab is already grouped", outcome.Detail);
    }

    [Fact]
    public void Group_tab_refuses_a_title_that_cannot_be_read_back()
    {
        var outcome = Over(new ScriptedJxa()).GroupTab(Tab(), "Work – 3 tabs", isNew: true);

        Assert.False(outcome.Grouped);
        Assert.Equal("Group title is empty or cannot be read unambiguously", outcome.Detail);
    }

    [Fact]
    public void Ungroup_refuses_a_tab_no_longer_in_the_named_group()
    {
        var outcome = Over(new ScriptedJxa()).Ungroup(Tab() with { Group = new TabGroup(new RuntimeId([1, 2, 3, 4, -1]), "Other") }, "Work");

        Assert.False(outcome.Grouped);
        Assert.Equal("Tab is no longer in the requested group", outcome.Detail);
    }

    [Fact]
    public void Move_refuses_a_tab_already_in_the_target_window()
    {
        var outcome = Over(new ScriptedJxa()).MoveToWindow(Tab(), WindowId);

        Assert.False(outcome.Outcome.Grouped);
        Assert.Equal("Move requires an ungrouped tab and a different window", outcome.Outcome.Detail);
        Assert.Null(outcome.Tab);
    }

    [Fact]
    public void List_windows_retries_an_unsettled_read_and_keeps_the_last_snapshot()
    {
        var changed = RawWindow() with { Title = "Changed" };
        var jxa = new ScriptedJxa()
            .Snapshot(Snapshot(RawWindow()))
            .Snapshot(Snapshot(changed))
            .Snapshot(Snapshot(changed));

        var tab = Assert.Single(Assert.Single(Over(jxa).ListWindows()).Tabs);

        Assert.Equal(Title, tab.Title);
        Assert.Equal(3, jxa.SnapshotCount);
    }

    [Fact]
    public void List_windows_falls_back_to_protected_tabs_without_accessibility()
    {
        _api.IsTrusted = false;
        var (mac, _) = Over(_api, Snapshot(RawWindow()));

        var tab = Assert.Single(Assert.Single(mac.ListWindows()).Tabs);

        Assert.Contains(MacRead.MetadataUnavailable, tab.Alerts);
        Assert.True(tab.Pinned);
        Assert.True(tab.Audible);
    }

    private const string PrTitle = "Fix paging · Pull Request #12 · acme-corp/data-platform";
    private const string PrAddress = "https://github.com/acme-corp/data-platform/pull/12/files";

    // Accessibility denied: the notes tab is active and a PR page sits beside it, both with placeholder flags.
    private static JxaSnapshot WithPrBeside() =>
        Snapshot(RawWindow() with { Tabs = [new JxaTab(TabId, Title, Address), new JxaTab(702, PrTitle, PrAddress)] });

    private static ChromeTab PrTab(bool selected = false) =>
        new(new RuntimeId([702]), WindowId, 1, PrTitle, PrTitle, selected, true, true, true)
        {
            Url = new Uri(PrAddress),
            Alerts = [MacRead.MetadataUnavailable],
        };

    [Fact]
    public void Close_tab_closes_a_background_pr_page_without_accessibility_evidence()
    {
        _api.IsTrusted = false;
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()) }
            .Snapshot(WithPrBeside())
            .Snapshot(WithPrBeside())
            .Reply("invoked");

        var outcome = Over(jxa).CloseTab(PrTab());

        Assert.True(outcome.Closed, outcome.Detail);
        Assert.Contains(PrAddress, jxa.Scripts.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Close_tab_refuses_a_pr_page_planned_with_evidence_that_has_since_lost_it()
    {
        _api.IsTrusted = false;
        var jxa = new ScriptedJxa { FallbackSnapshot = WithPrBeside() };

        var outcome = Over(jxa).CloseTab(PrTab() with { Pinned = false, Audible = false, Alerts = [] });

        Assert.Equal((false, $"{MacRead.MetadataUnavailable}; refusing close"), (outcome.Closed, outcome.Detail));
        Assert.Empty(jxa.Scripts);
    }

    [Fact]
    public void Close_tab_refuses_a_page_without_accessibility_evidence_that_is_not_a_pr()
    {
        _api.IsTrusted = false;
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()) };

        var outcome = Over(jxa).CloseTab(Tab());

        Assert.False(outcome.Closed);
        Assert.Equal($"{MacRead.MetadataUnavailable}; refusing close", outcome.Detail);
        Assert.Empty(jxa.Scripts);
    }

    [Fact]
    public void Close_tab_refuses_a_changed_title()
    {
        var jxa = new ScriptedJxa().Snapshot(Snapshot(RawWindow())).Snapshot(Snapshot(RawWindow()));

        var outcome = Over(jxa).CloseTab(Tab() with { Title = "Old title" });

        Assert.False(outcome.Closed);
        Assert.Equal("title changed to 'Notes'", outcome.Detail);
        Assert.Empty(jxa.Scripts);
    }

    [Fact]
    public void Close_tab_refuses_a_changed_URL()
    {
        var jxa = new ScriptedJxa().Snapshot(Snapshot(RawWindow())).Snapshot(Snapshot(RawWindow()));

        var outcome = Over(jxa).CloseTab(Tab() with { Url = new Uri("https://example.com/other") });

        Assert.False(outcome.Closed);
        Assert.Equal("URL changed; refusing close", outcome.Detail);
        Assert.Empty(jxa.Scripts);
    }

    [Fact]
    public void Close_tab_refuses_a_tab_whose_native_metadata_is_unavailable()
    {
        _tab.Attributes["AXRole"] = "AXButton";
        _tab.Attributes.Remove("AXSubrole");
        var jxa = new ScriptedJxa().Snapshot(Snapshot(RawWindow())).Snapshot(Snapshot(RawWindow()));

        var outcome = Over(jxa).CloseTab(Tab());

        Assert.False(outcome.Closed);
        Assert.Equal("Accessibility metadata unavailable; refusing close", outcome.Detail);
        Assert.Empty(jxa.Scripts);
    }

    [Fact]
    public void Close_tab_reports_a_tab_that_has_gone()
    {
        var jxa = new ScriptedJxa().Snapshot(Snapshot(RawWindow())).Snapshot(Snapshot(RawWindow()));

        var outcome = Over(jxa).CloseTab(Tab() with { RuntimeId = new RuntimeId([999]) });

        Assert.False(outcome.Closed);
        Assert.Equal("tab no longer present", outcome.Detail);
        Assert.Empty(jxa.Scripts);
    }

    [Fact]
    public void Close_tab_reports_a_failed_script()
    {
        var jxa = new ScriptedJxa { FailAct = true };
        _ = jxa.Snapshot(Snapshot(RawWindow())).Snapshot(Snapshot(RawWindow()));

        var outcome = Over(jxa).CloseTab(Tab());

        Assert.False(outcome.Closed);
        Assert.Equal("close failed: denied", outcome.Detail);
    }

    [Fact]
    public void Restore_selection_keeps_the_original_when_the_script_selects_it()
    {
        var original = Tab();
        var jxa = new ScriptedJxa().Reply("selected");

        var restored = Over(jxa).RestoreSelection([ChromeWindowWith(original)]);

        Assert.Equal(original, restored[WindowId]);
    }

    [Fact]
    public void Restore_selection_stops_on_an_unexpected_reply()
    {
        var jxa = new ScriptedJxa().Reply("refused").Reply("selected");

        var restored = Over(jxa).RestoreSelection([ChromeWindowWith(Tab())], new Dictionary<long, ChromeTab> { [WindowId] = Tab() with { RuntimeId = new RuntimeId([702]) } });

        Assert.Null(restored[WindowId]);
        Assert.Single(jxa.Scripts);
    }

    [Fact]
    public void Restore_selection_leaves_a_window_with_no_tab_alone()
    {
        var restored = Over(new ScriptedJxa()).RestoreSelection([ChromeWindowWith()]);

        Assert.Null(restored[WindowId]);
    }

    [Fact]
    public void Foreground_arrives_then_restores_the_cursor()
    {
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()), ActFallback = "invoked" };
        using var lease = Over(jxa).Foreground(WindowId);

        Assert.True(lease.Arrived);

        lease.Dispose();

        Assert.Equal([(5.0, 6.0)], _api.Warps);
        Assert.Contains(0L, _api.Fronted);
        Assert.Equal(2, jxa.Scripts.Count);
    }

    [Fact]
    public void Foreground_is_false_for_a_window_that_is_not_open()
    {
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()) };
        using var lease = Over(jxa).Foreground(99);

        Assert.False(lease.Arrived);
    }

    [Fact]
    public void Foreground_is_false_without_accessibility()
    {
        _api.IsTrusted = false;
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()) };
        using var lease = Over(jxa).Foreground(WindowId);

        Assert.False(lease.Arrived);
    }

    [Fact]
    public void Activate_reports_a_refused_script()
    {
        var jxa = new ScriptedJxa { ActFallback = "missing" };

        Assert.False(Over(jxa).Activate(WindowId, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Activate_times_out_when_the_window_does_not_arrive()
    {
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow() with { Id = 43 }), ActFallback = "invoked" };

        Assert.False(Over(jxa).Activate(WindowId, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Group_tab_times_out_waiting_for_its_menu()
    {
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()), ActFallback = "invoked" };

        var outcome = Over(jxa).GroupTab(Tab(), "Work", isNew: true);

        Assert.False(outcome.Grouped);
        Assert.Equal("Timed out waiting for Chrome UI confirmation", outcome.Detail);
    }

    [Fact]
    public void Group_tab_refuses_a_new_group_outside_its_home_window()
    {
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()), ActFallback = "invoked" };

        var outcome = Over(jxa).GroupTab(Tab(), "Work", isNew: true, homeHandle: 99);

        Assert.False(outcome.Grouped);
        Assert.Equal("New group needs its home window and a unique title", outcome.Detail);
    }

    [Fact]
    public void Group_tab_refuses_an_existing_group_that_is_not_open()
    {
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()), ActFallback = "invoked" };

        var outcome = Over(jxa).GroupTab(Tab(), "Work", isNew: false);

        Assert.False(outcome.Grouped);
        Assert.Equal("Target group missing or ambiguous", outcome.Detail);
    }

    [Fact]
    public void Group_tab_creates_a_new_group_and_confirms_its_membership()
    {
        var field = new FakeAxApi.Element(60, new()
        {
            ["AXRole"] = "AXTextField",
            ["AXTitle"] = "Tab-group title",
            ["AXValue"] = string.Empty,
            ["AXFocused"] = true,
        });
        var bubble = new FakeAxApi.Element(61, new()
        {
            ["AXRole"] = "AXGroup",
            ["AXChildren"] = new[] { field },
        });
        var item = new FakeAxApi.Element(50, new()
        {
            ["AXRole"] = "AXMenuItem",
            ["AXTitle"] = "Add Tab to New Group",
            ["AXEnabled"] = true,
        });
        var menu = new FakeAxApi.Element(51, new()
        {
            ["AXRole"] = "AXMenu",
            ["AXChildren"] = new[] { item },
        });
        var header = new FakeAxApi.Element(70, new()
        {
            ["AXRole"] = "AXTabGroup",
            ["AXTitle"] = " unnamed group – 1 tab",
            ["AXExpanded"] = true,
        });
        _api.App["AXChildren"] = new[] { _window, bubble };
        var menuShown = false;
        var grouped = false;
        var renamed = false;
        _api.OnRead = (attribute, _) =>
        {
            if (attribute != "AXRole")
            {
                return;
            }

            if (!menuShown && _api.Actions.Any(action => action.StartsWith("AXShowMenu@", StringComparison.Ordinal)))
            {
                menuShown = true;
                _api.App["AXChildren"] = new[] { _window, bubble, menu };
            }

            if (!grouped && _api.Actions.Any(action => action.StartsWith("AXPress@50", StringComparison.Ordinal)))
            {
                grouped = true;
                _strip.Attributes["AXChildren"] = new[] { header, _tab };
            }
        };
        _api.OnPost = posted =>
        {
            if (!renamed && posted.Description.StartsWith("key 36", StringComparison.Ordinal))
            {
                renamed = true;
                header.Attributes["AXTitle"] = " group Work – 1 tab";
            }
        };
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()), ActFallback = "invoked" };

        var outcome = Over(jxa).GroupTab(Tab(), "Work", isNew: true);

        Assert.True(outcome.Grouped);
        Assert.Equal("created", outcome.Detail);
        Assert.True(renamed);
    }

    [Fact]
    public void Ungroup_dissolves_the_named_group()
    {
        var header = new FakeAxApi.Element(70, new()
        {
            ["AXRole"] = "AXTabGroup",
            ["AXTitle"] = " group Work – 1 tab",
            ["AXExpanded"] = true,
        });
        _strip.Attributes["AXChildren"] = new[] { header, _tab };
        var field = new FakeAxApi.Element(60, new()
        {
            ["AXRole"] = "AXTextField",
            ["AXTitle"] = "Tab-group title",
            ["AXValue"] = string.Empty,
        });
        var button = new FakeAxApi.Element(62, new()
        {
            ["AXRole"] = "AXButton",
            ["AXTitle"] = "Ungroup",
            ["AXEnabled"] = true,
        });
        var bubble = new FakeAxApi.Element(61, new()
        {
            ["AXRole"] = "AXGroup",
            ["AXChildren"] = new[] { field, button },
        });
        var confirm = new FakeAxApi.Element(63, new()
        {
            ["AXRole"] = "AXButton",
            ["AXTitle"] = "Ungroup",
            ["AXEnabled"] = true,
        });
        var dialog = new FakeAxApi.Element(85, new()
        {
            ["AXRole"] = "AXDialog",
            ["AXTitle"] = "Ungroup Tab Group?",
            ["AXChildren"] = new[] { confirm },
        });
        var sheet = new FakeAxApi.Element(84, new()
        {
            ["AXRole"] = "AXSheet",
            ["AXTitle"] = "Ungroup Tab Group?",
            ["AXChildren"] = new[] { dialog },
        });
        _api.App["AXChildren"] = new[]
        {
            _window,
            bubble,
            sheet,
            new FakeAxApi.Element(80, new() { ["AXRole"] = "AXGroup", ["AXSubrole"] = "AXApplicationDialog" }),
            new FakeAxApi.Element(81, new() { ["AXRole"] = "AXGroup", ["AXSubrole"] = "AXApplicationAlertDialog" }),
            new FakeAxApi.Element(82, new() { ["AXRole"] = "AXGroup", ["AXSubrole"] = "AXDialog" }),
            new FakeAxApi.Element(83, new() { ["AXRole"] = "AXGroup", ["AXSubrole"] = "AXSystemDialog" }),
        };
        _api.OnRead = (attribute, _) =>
        {
            if (attribute == "AXRole" && _api.Actions.Any(action => action.StartsWith("AXPress@63", StringComparison.Ordinal)))
            {
                _strip.Attributes["AXChildren"] = new[] { _tab };
            }
        };
        var group = new TabGroup(MacRead.GroupId(WindowId, 70), "Work");
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()), ActFallback = "invoked" };

        var outcome = Over(jxa).Ungroup(Tab() with { Group = group }, "Work");

        Assert.True(outcome.Grouped, outcome.Detail);
        Assert.Equal("ungrouped", outcome.Detail);
    }

    [Fact]
    public void Move_moves_an_ungrouped_tab_to_the_other_window()
    {
        var close = new FakeAxApi.Element(23, new()
        {
            ["AXRole"] = "AXButton",
            ["AXTitle"] = "Close",
        });
        var targetTab = new FakeAxApi.Element(22, new()
        {
            ["AXRole"] = "AXRadioButton",
            ["AXSubrole"] = "AXTabButton",
            ["AXTitle"] = "Target - Memory usage - 64 MB",
            ["AXValue"] = true,
            ["AXChildren"] = new[] { close },
        });
        var targetStrip = new FakeAxApi.Element(21, new()
        {
            ["AXRole"] = "AXTabGroup",
            ["AXChildren"] = new[] { targetTab },
        });
        var targetAvatar = new FakeAxApi.Element(20, new()
        {
            ["AXRole"] = "AXButton",
            ["AXIdentifier"] = "AvatarToolbarButton",
            ["AXTitle"] = "Work",
        });
        var targetWindow = new FakeAxApi.Element(2, new()
        {
            ["AXRole"] = "AXWindow",
            ["AXTitle"] = "Target window",
            ["AXChildren"] = new[] { targetAvatar, targetStrip },
        });
        var parent = new FakeAxApi.Element(50, new()
        {
            ["AXRole"] = "AXMenuItem",
            ["AXTitle"] = "Move Tab to Another Window",
            ["AXEnabled"] = true,
        });
        var entry = new FakeAxApi.Element(52, new()
        {
            ["AXRole"] = "AXMenuItem",
            ["AXTitle"] = "Target",
            ["AXEnabled"] = true,
        });
        var submenu = new FakeAxApi.Element(51, new()
        {
            ["AXRole"] = "AXMenu",
            ["AXChildren"] = new[] { entry },
        });
        parent.Attributes["AXChildren"] = new[] { submenu };
        var menu = new FakeAxApi.Element(53, new()
        {
            ["AXRole"] = "AXMenu",
            ["AXChildren"] = new[] { parent },
        });
        _api.App["AXWindows"] = new[] { _window, targetWindow };
        _api.App["AXChildren"] = new[] { _window, targetWindow };
        var target = new JxaWindow(43, 2, "Target window", 1, false, "normal", [new JxaTab(702, "Target", "https://example.com/target")]);
        var moved = new JxaWindow(43, 1, "Target window", 1, false, "normal", [new JxaTab(702, "Target", "https://example.com/target"), new JxaTab(TabId, Title, Address)]);
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow(), target), ActFallback = "invoked" };
        var menuShown = false;
        _api.OnRead = (attribute, _) =>
        {
            if (attribute != "AXRole")
            {
                return;
            }

            if (!menuShown && _api.Actions.Any(action => action.StartsWith("AXShowMenu@", StringComparison.Ordinal)))
            {
                menuShown = true;
                _api.App["AXChildren"] = new[] { _window, targetWindow, menu };
            }

            if (_api.Actions.Any(action => action.StartsWith("AXPress@52", StringComparison.Ordinal)))
            {
                _strip.Attributes["AXChildren"] = Array.Empty<FakeAxApi.Element>();
                _tab.Attributes["AXValue"] = false;
                targetStrip.Attributes["AXChildren"] = new[] { targetTab, _tab };
                jxa.FallbackSnapshot = Snapshot(moved);
            }
        };

        var outcome = Over(jxa).MoveToWindow(Tab(), 43);

        Assert.True(outcome.Outcome.Grouped, outcome.Outcome.Detail);
        Assert.Equal("moved", outcome.Outcome.Detail);
        Assert.Equal(43, outcome.Tab!.WindowHandle);
    }

    [Fact]
    public void Move_refuses_a_target_window_that_is_not_open()
    {
        var jxa = new ScriptedJxa { FallbackSnapshot = Snapshot(RawWindow()), ActFallback = "invoked" };

        var outcome = Over(jxa).MoveToWindow(Tab(), 99);

        Assert.False(outcome.Outcome.Grouped);
        Assert.Equal("Window missing or ambiguous", outcome.Outcome.Detail);
        Assert.Null(outcome.Tab);
    }

    [Theory]
    [InlineData("Target", true)]
    [InlineData("Target and 1 other tab", true)]
    [InlineData("Target and 2 other tabs", true)]
    [InlineData("Target and 1 Other Tab", true)]
    [InlineData("Target and 1 other tab and 1 other tab", false)]
    [InlineData("Different", false)]
    [InlineData("Targ…", true)]
    [InlineData("…", false)]
    [InlineData("Target…", true)]
    [InlineData("Other…", false)]
    public void A_window_label_names_the_active_tab_and_its_other_tabs(string name, bool expected)
    {
        var tabs = new List<ChromeTab>
        {
            new(new RuntimeId([1]), 1, 0, "Target", "Target", true, false, true, false),
        };
        var extra = name.Contains("2 other tabs", StringComparison.Ordinal) ? 2
            : name.Contains("other tab", StringComparison.Ordinal) || name.Contains("Other Tab", StringComparison.Ordinal) ? 1
            : 0;
        for (var index = 0; index < extra; index++)
        {
            tabs.Add(new(new RuntimeId([index + 2]), 1, index + 1, "Another", "Another", false, false, true, false));
        }

        var window = new ChromeWindow(1, "Window", "Default", null, 1, false, null, tabs);

        Assert.Equal(expected, ChromeMac.NamesWindow(name, window));
    }

    [Fact]
    public void A_window_with_no_selected_tab_is_never_named()
    {
        var window = new ChromeWindow(1, "Window", "Default", null, 1, false, null, [Tab() with { Selected = false }]);

        Assert.False(ChromeMac.NamesWindow("Notes", window));
    }

    [Fact]
    public void A_newline_in_the_active_title_is_ignored()
    {
        var tab = Tab() with { Title = "Notes\nline" };
        var window = ChromeWindowWith(tab);

        Assert.True(ChromeMac.NamesWindow("Notesline", window));
    }

    [Fact]
    public void Saved_groups_are_empty_and_deleting_one_is_refused()
    {
        var mac = Over(new ScriptedJxa());

        Assert.Empty(mac.SavedGroups(WindowId, "Profile 4"));
        var outcome = mac.DeleteSavedGroup(WindowId, "Profile 4", "Work");
        Assert.False(outcome.Deleted);
        Assert.Equal("saved group 'Work': not supported on macOS", outcome.Detail);
        Assert.Empty(outcome.Tabs);
    }

    private static ChromeTab Tab() =>
        new(new RuntimeId([(int)TabId]), WindowId, 0, $"{Title} - Memory usage - 128 MB", Title, true, false, true, false)
        {
            Url = new Uri(Address),
        };

    private static ChromeWindow ChromeWindowWith(params ChromeTab[] tabs) =>
        new(WindowId, "Chrome work", "Profile 4", "Work", 1, false, null, tabs);

    /// <summary>Every clock read moves a second on, so a poll that would wait times out in a handful of turns.</summary>
    private sealed class AdvancingTime : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp += TimeSpan.TicksPerSecond;
    }

    /// <summary>A scripting bridge answering from explicit replies, then from fallbacks; no real osascript runs.</summary>
    private sealed class ScriptedJxa : IChromeJxa
    {
        private readonly Queue<JxaSnapshot> _snapshots = new();
        private readonly Queue<string> _acts = new();

        public List<string> Scripts { get; } = [];

        public int SnapshotCount { get; private set; }

        public bool FailAct { get; init; }

        public JxaSnapshot? FallbackSnapshot { get; set; }

        public string ActFallback { get; set; } = "missing";

        public ScriptedJxa Snapshot(JxaSnapshot snapshot)
        {
            _snapshots.Enqueue(snapshot);
            return this;
        }

        public ScriptedJxa Reply(string status)
        {
            _acts.Enqueue(status);
            return this;
        }

        public JxaSnapshot Snapshot()
        {
            SnapshotCount++;
            return _snapshots.Count > 0 ? _snapshots.Dequeue() : FallbackSnapshot ?? throw new InvalidOperationException("No scripted snapshot left");
        }

        public int? FrontmostPid() => 501;

        public string Act(string script)
        {
            if (FailAct)
            {
                throw new ChromeMacException("denied");
            }

            Scripts.Add(script);
            return _acts.Count > 0 ? _acts.Dequeue() : ActFallback;
        }
    }
}
