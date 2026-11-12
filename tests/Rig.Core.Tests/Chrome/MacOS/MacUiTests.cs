using Rig.Core.Chrome.MacOS;

namespace Rig.Core.Tests.Chrome.MacOS;

/// <summary>
/// Retained elements and Quartz input over a fake of the native calls: no real element is acted on and no real event is
/// posted. The fake counts every reference, so each test can check what the session still holds.
/// </summary>
public sealed class MacUiTests : IDisposable
{
    private const int Pid = 123;

    private readonly FakeAxApi _api = new();
    private readonly FakeAxApi.Element _button;
    private readonly FakeAxApi.Element _field;
    private readonly MacUi _ui;

    public MacUiTests()
    {
        _button = new FakeAxApi.Element(10, new()
        {
            ["AXRole"] = "AXButton",
            ["AXTitle"] = "Work",
            ["AXEnabled"] = true,
            ["AXFocused"] = false,
            ["AXPosition"] = new FakeAxApi.Pair(AxGeometry.Point, 100, 200),
            ["AXSize"] = new FakeAxApi.Pair(AxGeometry.Size, 40, 20),
        });
        _field = new FakeAxApi.Element(11, new()
        {
            ["AXRole"] = "AXTextField",
            ["AXDescription"] = "Tab-group title",
            ["AXValue"] = string.Empty,
            ["AXFocused"] = true,
        });
        var window = new FakeAxApi.Element(20, new()
        {
            ["AXRole"] = "AXWindow",
            ["AXTitle"] = "Chrome",
            ["AXChildren"] = new[] { _button, _field },
        });
        _api.App["AXRole"] = "AXApplication";
        _api.App["AXFrontmost"] = true;
        _api.App["AXWindows"] = new[] { window };
        _api.App["AXChildren"] = new[] { window };
        _ui = MacUi.Open(_api);
    }

    public void Dispose() => _ui.Dispose();

    private AxNode Tree() => _ui.ApplicationTree(Pid);

    private AxNode Node(string name) => Find(Tree(), name) ?? throw new InvalidOperationException(name);

    private static AxNode? Find(AxNode node, string name) =>
        node.Name == name ? node : node.Children.Select(child => Find(child, name)).FirstOrDefault(found => found is not null);

    private Dictionary<nint, int> Held() => new(_api.Owned);

    private List<string> Posted() => [.. _api.Posted.Select(posted => posted.ToString())];

    [Fact]
    public void A_session_needs_permission()
    {
        _api.IsTrusted = false;
        var error = Assert.Throws<AccessibilityUnavailableException>(() => MacUi.Open(_api));
        Assert.Equal("Grant Accessibility access to the terminal running chrome-tabs", error.Message);
        _ = Assert.Throws<ArgumentNullException>(() => MacUi.Open(null!));
    }

    [Fact]
    public void Every_decoded_element_is_retained_until_the_session_ends()
    {
        var tree = Tree();

        Assert.Equal("AXApplication", tree.Role);
        Assert.Equal(["Work", "Tab-group title"], tree.Children[0].Children.Select(node => node.Name));
        Assert.Equal(1.0f, _api.Timeout);
        Assert.Equal(4, _api.Owned.Count);
        _ui.Dispose();
        Assert.Empty(_api.Owned);
        _ui.Dispose();
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void A_tree_without_an_application_element_is_unavailable()
    {
        _api.NoApplication = true;
        var error = Assert.Throws<AccessibilityUnavailableException>(Tree);
        Assert.Equal("Chrome AX application is unavailable", error.Message);
    }

    [Fact]
    public void A_decode_that_fails_partway_still_releases_what_it_retained()
    {
        _field.Attributes["AXSubrole"] = new FakeAxApi.Failure(-25200);

        _ = Assert.Throws<AccessibilityUnavailableException>(Tree);

        Assert.Single(_api.Owned); // the button, retained before the field failed
        _ui.Dispose();
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void A_session_retains_at_most_its_limit()
    {
        var leaf = new FakeAxApi.Element(30, new() { ["AXRole"] = "AXWebArea" });
        _api.App["AXChildren"] = Enumerable.Repeat(leaf, AxReader.Budget - 1).ToArray();
        for (var read = 0; read < MacUi.RetainLimit / AxReader.Budget; read++)
        {
            _ = Tree();
        }

        var error = Assert.Throws<AccessibilityUnavailableException>(Tree);
        Assert.Equal("AX action exceeded its retained reference limit", error.Message);
    }

    [Fact]
    public void Browser_windows_are_retained_for_actions()
    {
        var window = Assert.Single(_ui.BrowserWindows(Pid));
        var button = window.Children[0];

        _ui.Press(button);

        Assert.Equal(["AXPress@10"], _api.Actions);
    }

    [Fact]
    public void Frontmost_asks_accessibility_and_releases_the_application()
    {
        Assert.True(_ui.Frontmost(Pid));
        _api.App["AXFrontmost"] = 0L;
        Assert.False(_ui.Frontmost(Pid));
        _ = _api.App.Remove("AXFrontmost");
        Assert.False(_ui.Frontmost(Pid));
        _api.NoApplication = true;
        Assert.False(_ui.Frontmost(Pid));
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void Only_nodes_this_session_decoded_can_be_acted_on()
    {
        var button = Node("Work");

        Assert.Equal("AX element is not owned by this action session", Assert.Throws<AccessibilityUnavailableException>(() => _ui.Press(button with { })).Message);
        Assert.Equal("AX element is not owned by this action session", Assert.Throws<AccessibilityUnavailableException>(() => _ui.Press(new AxNode(10, "AXButton", Title: "Work"))).Message);
        _ = Assert.Throws<ArgumentNullException>(() => _ui.Press(null!));
        Assert.Empty(_api.Actions);
    }

    [Fact]
    public void A_control_that_changed_is_refused()
    {
        var button = Node("Work");
        var field = Node("Tab-group title");

        _button.Attributes["AXTitle"] = "Home";
        Assert.Equal("AX element changed name", Assert.Throws<AccessibilityUnavailableException>(() => _ui.Press(button)).Message);
        _button.Attributes["AXRole"] = "AXMenuItem";
        Assert.Equal("AX element changed role", Assert.Throws<AccessibilityUnavailableException>(() => _ui.Press(button)).Message);
        _field.Attributes["AXDescription"] = "Other";
        Assert.Equal("AX element changed name", Assert.Throws<AccessibilityUnavailableException>(() => _ui.Focused(field)).Message);
        Assert.Empty(_api.Actions);
    }

    [Fact]
    public void Press_rechecks_that_the_control_is_enabled()
    {
        var button = Node("Work");
        _button.Attributes["AXEnabled"] = false;
        Assert.Equal("AX control is not enabled", Assert.Throws<AccessibilityUnavailableException>(() => _ui.Press(button)).Message);
        _ = _button.Attributes.Remove("AXEnabled");
        _ = Assert.Throws<AccessibilityUnavailableException>(() => _ui.Press(button));
        Assert.Empty(_api.Actions);

        _button.Attributes["AXEnabled"] = 1L;
        _ui.Press(button);
        Assert.Equal(["AXPress@10"], _api.Actions);
    }

    [Theory]
    [InlineData(-25202, true)]
    [InlineData(-25200, false)]
    public void A_failed_press_says_why(int code, bool invalid)
    {
        var button = Node("Work");
        _api.ActionErrors["AXPress"] = code;

        var error = Assert.ThrowsAny<AccessibilityUnavailableException>(() => _ui.Press(button));

        Assert.Equal($"AX action AXPress failed ({code})", error.Message);
        Assert.Equal(invalid, error is InvalidUiElementException);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-25204)]
    public void A_menu_shows_even_when_its_modal_loop_times_out(int code)
    {
        var button = Node("Work");
        _api.ActionErrors["AXShowMenu"] = code;

        _ui.ShowMenu(button);

        Assert.Equal(["AXShowMenu@10"], _api.Actions);
        Assert.Equal(0.5f, _api.Timeouts[10]);
    }

    [Theory]
    [InlineData(-25202, true)]
    [InlineData(-25200, false)]
    public void A_menu_that_fails_says_why(int code, bool invalid)
    {
        var button = Node("Work");
        _api.ActionErrors["AXShowMenu"] = code;

        var error = Assert.ThrowsAny<AccessibilityUnavailableException>(() => _ui.ShowMenu(button));

        Assert.Equal($"AX action AXShowMenu failed ({code})", error.Message);
        Assert.Equal(invalid, error is InvalidUiElementException);
    }

    [Fact]
    public void Focus_is_read_from_the_retained_element()
    {
        Assert.True(_ui.Focused(Node("Tab-group title")));
        Assert.False(_ui.Focused(Node("Work")));
    }

    [Fact]
    public void Text_is_typed_as_unicode_after_the_guard_and_a_focus_check()
    {
        var field = Node("Tab-group title");
        var held = Held();
        var guarded = 0;

        _ui.SetText(field, "雪🚀", () => guarded++);

        Assert.Equal(1, guarded);
        Assert.Equal(["key 0 down '雪🚀' flags 0 to tap 0", "key 0 up '雪🚀' flags 0 to tap 0"], Posted());
        Assert.Equal(held, _api.Owned);
    }

    [Fact]
    public void Text_goes_eight_characters_at_a_time_without_splitting_a_surrogate_pair()
    {
        var field = Node("Tab-group title");
        var guarded = 0;

        _ui.SetText(field, "abcdefg🚀xyz", () => guarded++);

        Assert.Equal(2, guarded);
        Assert.Equal(["abcdefg🚀", "abcdefg🚀", "xyz", "xyz"], _api.Posted.Select(posted => posted.Text));
    }

    [Fact]
    public void Empty_text_types_nothing()
    {
        _ui.SetText(Node("Tab-group title"), string.Empty, () => Assert.Fail("no chunk to guard"));
        Assert.Empty(_api.Posted);
    }

    [Fact]
    public void Invalid_text_is_refused_before_anything_is_read()
    {
        var field = Node("Tab-group title");
        var reads = _api.Reads.Count;

        var error = Assert.Throws<AccessibilityUnavailableException>(() => _ui.SetText(field, "a\uD800b", () => { }));

        Assert.Equal("Group title is not valid text", error.Message);
        Assert.Equal(reads, _api.Reads.Count);
        _ = Assert.Throws<ArgumentNullException>(() => _ui.SetText(field, null!, () => { }));
        _ = Assert.Throws<ArgumentNullException>(() => _ui.SetText(field, "a", null!));
    }

    [Fact]
    public void A_field_that_is_no_longer_empty_is_never_typed_into()
    {
        var field = Node("Tab-group title");
        _field.Attributes["AXValue"] = "Work";

        var error = Assert.Throws<AccessibilityUnavailableException>(() => _ui.SetText(field, "Home", () => Assert.Fail("guarded")));

        Assert.Equal("New group title field is no longer empty", error.Message);
        Assert.Empty(_api.Posted);
    }

    [Fact]
    public void Losing_focus_between_chunks_stops_typing()
    {
        var field = Node("Tab-group title");
        _api.OnPost = posted => _field.Attributes["AXFocused"] = false;

        var error = Assert.Throws<AccessibilityUnavailableException>(() => _ui.SetText(field, "abcdefghij", () => { }));

        Assert.Equal("Group title field lost focus", error.Message);
        Assert.Equal(["abcdefgh", "abcdefgh"], _api.Posted.Select(posted => posted.Text));
    }

    [Fact]
    public void A_guard_that_throws_stops_typing()
    {
        var field = Node("Tab-group title");
        _ = Assert.Throws<TimeoutException>(() => _ui.SetText(field, "abc", () => throw new TimeoutException()));
        Assert.Empty(_api.Posted);
    }

    [Fact]
    public void An_input_event_that_cannot_be_made_stops_typing()
    {
        var field = Node("Tab-group title");
        var held = Held();
        _ = _api.RefusedEvents.Add(1);

        var error = Assert.Throws<AccessibilityUnavailableException>(() => _ui.SetText(field, "abc", () => { }));

        Assert.Equal("Cannot create Unicode input event", error.Message);
        Assert.Single(_api.Posted);
        Assert.Equal(held, _api.Owned);
    }

    [Fact]
    public void A_post_that_fails_still_releases_its_event()
    {
        var field = Node("Tab-group title");
        var held = Held();
        _api.OnPost = posted => throw new IOException("posted");

        _ = Assert.Throws<IOException>(() => _ui.SetText(field, "abc", () => { }));

        Assert.Equal(held, _api.Owned);
    }

    [Fact]
    public void The_click_point_is_the_centre_when_the_control_is_what_is_there()
    {
        var button = Node("Work");
        var held = Held();
        _api.Hit = _button;

        Assert.Equal(new ScreenPos(120, 210), _ui.ClickPoint(button));
        Assert.Equal((120f, 210f), _api.HitAt);
        Assert.Equal(held, _api.Owned);
    }

    [Fact]
    public void A_click_on_a_child_of_the_control_lands_on_it()
    {
        var button = Node("Work");
        var held = Held();
        var child = _button;
        for (var hop = 0; hop < 11; hop++)
        {
            child = new FakeAxApi.Element(40 + hop, new() { ["AXParent"] = child });
        }

        _api.Hit = child;

        Assert.Equal(new ScreenPos(120, 210), _ui.ClickPoint(button));
        Assert.Equal(held, _api.Owned);
    }

    [Fact]
    public void A_click_on_anything_else_is_refused()
    {
        var button = Node("Work");
        var held = Held();
        var child = _button;
        for (var hop = 0; hop < 12; hop++)
        {
            child = new FakeAxApi.Element(40 + hop, new() { ["AXParent"] = child });
        }

        _api.Hit = child;
        Assert.Equal("A click would land on a different control", Assert.Throws<AccessibilityUnavailableException>(() => _ui.ClickPoint(button)).Message);
        Assert.Equal(held, _api.Owned);

        _api.Hit = new FakeAxApi.Element(50, []);
        Assert.Equal("A click would land on a different control", Assert.Throws<AccessibilityUnavailableException>(() => _ui.ClickPoint(button)).Message);
        Assert.Equal(held, _api.Owned);
    }

    [Fact]
    public void A_parent_that_cannot_be_read_releases_the_walk()
    {
        var button = Node("Work");
        var held = Held();
        _api.Hit = new FakeAxApi.Element(50, new() { ["AXParent"] = new FakeAxApi.Failure(-25200) });

        Assert.Equal("AX read failed (-25200)", Assert.Throws<AccessibilityUnavailableException>(() => _ui.ClickPoint(button)).Message);
        Assert.Equal(held, _api.Owned);
    }

    [Theory]
    [InlineData(-25200, true)]
    [InlineData(-25200, false)]
    [InlineData(0, false)]
    public void A_click_point_nothing_answers_for_is_refused(int code, bool found)
    {
        var button = Node("Work");
        var held = Held();
        _api.HitError = code;
        _api.Hit = found ? _button : null;

        Assert.Equal("Cannot identify the control at the click point", Assert.Throws<AccessibilityUnavailableException>(() => _ui.ClickPoint(button)).Message);
        Assert.Equal(held, _api.Owned);
    }

    [Fact]
    public void Without_a_system_wide_element_the_click_point_is_refused()
    {
        var button = Node("Work");
        _api.System = null;
        Assert.Equal("Cannot identify the control at the click point", Assert.Throws<AccessibilityUnavailableException>(() => _ui.ClickPoint(button)).Message);
    }

    [Theory]
    [InlineData("AXPosition", "Missing AXPosition")]
    [InlineData("AXSize", "Missing AXSize")]
    public void A_control_without_geometry_has_no_click_point(string attribute, string message)
    {
        var button = Node("Work");
        _ = _button.Attributes.Remove(attribute);
        Assert.Equal(message, Assert.Throws<AccessibilityUnavailableException>(() => _ui.ClickPoint(button)).Message);
    }

    [Fact]
    public void Geometry_of_the_wrong_kind_is_invalid()
    {
        var button = Node("Work");
        var held = Held();
        _button.Attributes["AXSize"] = new FakeAxApi.Pair(AxGeometry.Point, 40, 20);

        Assert.Equal("Invalid AXSize", Assert.Throws<AccessibilityUnavailableException>(() => _ui.ClickPoint(button)).Message);
        Assert.Equal(held, _api.Owned);
    }

    [Theory]
    [InlineData(0, 0, 0, 20)]
    [InlineData(0, 0, 40, 0)]
    [InlineData(double.NaN, 0, 40, 20)]
    [InlineData(0, double.PositiveInfinity, 40, 20)]
    [InlineData(0, 0, double.PositiveInfinity, 20)]
    [InlineData(0, 0, 40, double.PositiveInfinity)]
    [InlineData(0, 0, double.NaN, 20)]
    public void A_control_with_no_visible_rectangle_has_no_click_point(double x, double y, double width, double height)
    {
        var button = Node("Work");
        _button.Attributes["AXPosition"] = new FakeAxApi.Pair(AxGeometry.Point, x, y);
        _button.Attributes["AXSize"] = new FakeAxApi.Pair(AxGeometry.Size, width, height);
        _api.Hit = _button;

        Assert.Equal("AX control has no visible rectangle", Assert.Throws<AccessibilityUnavailableException>(() => _ui.ClickPoint(button)).Message);
        Assert.Null(_api.HitAt);
    }

    [Fact]
    public void The_cursor_is_read_and_restored()
    {
        Assert.Equal(new ScreenPos(5, 6), _ui.Cursor());
        _ui.RestoreCursor(new ScreenPos(7, 8));
        Assert.Equal([(7.0, 8.0)], _api.Warps);

        _api.CursorAt = null;
        Assert.Equal("Cannot read cursor position", Assert.Throws<AccessibilityUnavailableException>(() => _ui.Cursor()).Message);
        _api.WarpError = 1001;
        Assert.Equal("Cannot restore cursor position", Assert.Throws<AccessibilityUnavailableException>(() => _ui.RestoreCursor(new ScreenPos(7, 8))).Message);
        _ = Assert.Throws<ArgumentNullException>(() => _ui.RestoreCursor(null!));
    }

    [Fact]
    public void A_right_click_is_down_then_up_at_the_point()
    {
        _ui.RightClick(new ScreenPos(120, 210));

        Assert.Equal(["mouse 3 at 120,210 flags 0 to tap 0", "mouse 4 at 120,210 flags 0 to tap 0"], Posted());
        Assert.Empty(_api.Owned);
        _ = Assert.Throws<ArgumentNullException>(() => _ui.RightClick(null!));
    }

    [Fact]
    public void A_mouse_event_that_cannot_be_made_is_refused()
    {
        _ = _api.RefusedEvents.Add(0);
        Assert.Equal("Cannot create native input event", Assert.Throws<AccessibilityUnavailableException>(() => _ui.RightClick(new ScreenPos(1, 2))).Message);
        Assert.Empty(_api.Posted);
    }

    [Fact]
    public void A_key_is_pressed_and_released()
    {
        _ui.Key(36);
        Assert.Equal(["key 36 down flags 0 to tap 0", "key 36 up flags 0 to tap 0"], Posted());
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void Escape_goes_to_the_browser_alone()
    {
        _ui.Escape(Pid);
        Assert.Equal(["key 53 down flags 0 to pid 123", "key 53 up flags 0 to pid 123"], Posted());
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void An_escape_that_cannot_be_made_is_refused()
    {
        _ = _api.RefusedEvents.Add(1);
        Assert.Equal("Cannot create Escape event", Assert.Throws<AccessibilityUnavailableException>(() => _ui.Escape(Pid)).Message);
        Assert.Single(_api.Posted);
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void An_escape_that_fails_to_post_still_releases_its_event()
    {
        _api.OnPost = posted => throw new IOException("posted");
        _ = Assert.Throws<IOException>(() => _ui.Escape(Pid));
        Assert.Empty(_api.Owned);
    }

    [Fact]
    public void Focus_is_captured_from_the_system_and_kept_until_the_session_ends()
    {
        var window = new FakeAxApi.Element(61, []);
        var app = new FakeAxApi.Element(60, new() { ["AXFocusedWindow"] = window });
        _api.System = new FakeAxApi.Element(-1, new() { ["AXFocusedApplication"] = app });

        var focus = _ui.CaptureFocus(() => throw new InvalidOperationException("no fallback"));

        Assert.NotNull(focus.Window);
        Assert.Equal(2, _api.Owned.Count);
        _ui.RestoreFocus(focus);
        Assert.Equal(["AXRaise@61"], _api.Actions);
        Assert.Equal([60L], _api.Fronted);
        _ui.Dispose();
        Assert.Empty(_api.Owned);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_slow_focused_app_falls_back_to_the_frontmost_process(bool hasSystem)
    {
        _api.System = hasSystem ? new FakeAxApi.Element(-1, new() { ["AXFocusedApplication"] = new FakeAxApi.Failure(-25204) }) : null;
        _api.App["AXFocusedWindow"] = new FakeAxApi.Failure(-25204);

        var focus = _ui.CaptureFocus(() => Pid);

        Assert.Null(focus.Window);
        Assert.Single(_api.Owned);
        _ui.RestoreFocus(focus);
        Assert.Empty(_api.Actions);
        Assert.Equal([0L], _api.Fronted);
    }

    [Fact]
    public void A_focused_app_without_a_focused_window_restores_only_the_app()
    {
        _api.System = new FakeAxApi.Element(-1, new() { ["AXFocusedApplication"] = new FakeAxApi.Element(60, []) });
        var focus = _ui.CaptureFocus(() => null);
        Assert.Null(focus.Window);
        Assert.Single(_api.Owned);
    }

    [Fact]
    public void Focus_that_cannot_be_found_is_refused()
    {
        _api.System = new FakeAxApi.Element(-1, []);
        Assert.Equal("Cannot capture foreground application", Assert.Throws<AccessibilityUnavailableException>(() => _ui.CaptureFocus(() => null)).Message);
        _api.NoApplication = true;
        Assert.Equal("Cannot capture foreground application", Assert.Throws<AccessibilityUnavailableException>(() => _ui.CaptureFocus(() => Pid)).Message);
        Assert.Empty(_api.Owned);
        _ = Assert.Throws<ArgumentNullException>(() => _ui.CaptureFocus(null!));
    }

    [Theory]
    [InlineData(-25202, null)]
    [InlineData(-25211, "AX action AXRaise failed (-25211)")]
    [InlineData(-25204, "AX action AXRaise failed (-25204)")]
    public void The_app_comes_back_even_when_its_window_will_not(int code, string? message)
    {
        var app = new FakeAxApi.Element(60, new() { ["AXFocusedWindow"] = new FakeAxApi.Element(61, []) });
        _api.System = new FakeAxApi.Element(-1, new() { ["AXFocusedApplication"] = app });
        var focus = _ui.CaptureFocus(() => null);
        _api.ActionErrors["AXRaise"] = code;

        var error = Record.Exception(() => _ui.RestoreFocus(focus));

        Assert.Equal(message, error?.Message);
        Assert.Equal([60L], _api.Fronted);
    }

    [Fact]
    public void An_app_that_will_not_come_back_is_reported()
    {
        _api.System = new FakeAxApi.Element(-1, new() { ["AXFocusedApplication"] = new FakeAxApi.Element(60, []) });
        var focus = _ui.CaptureFocus(() => null);
        _api.FrontmostError = -25200;

        Assert.Equal("Cannot restore foreground application", Assert.Throws<AccessibilityUnavailableException>(() => _ui.RestoreFocus(focus)).Message);
        _ = Assert.Throws<ArgumentNullException>(() => _ui.RestoreFocus(null!));
    }

    [Fact]
    public void The_invalid_element_exception_keeps_its_message_and_cause()
    {
        var cause = new InvalidOperationException("cause");
        Assert.NotNull(new InvalidUiElementException().Message);
        Assert.Equal("gone", new InvalidUiElementException("gone").Message);
        Assert.Same(cause, new InvalidUiElementException("gone", cause).InnerException);
    }
}
