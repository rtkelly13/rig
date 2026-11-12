using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Chrome;
using Rig.Core.Chrome.MacOS;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Chrome.MacOS;

/// <summary>
/// Chrome's macOS scripting bridge over recorded osascript replies; osascript itself never runs. Replaces the JXA parts
/// of <c>test_chrome_macos_reader</c> and <c>test_chrome_macos_actions</c>.
/// </summary>
public sealed class ChromeJxaTests
{
    private const string Title = "Notes, \"雪\"\nline";

    // The reply the snapshot script gives for one minimised window holding one tab, as Python's tests recorded it.
    private static JsonObject Recorded(string title = Title) => new()
    {
        ["pid"] = 123,
        ["frontmost"] = true,
        ["windows"] = new JsonArray(new JsonObject
        {
            ["id"] = "42",
            ["index"] = 1,
            ["title"] = "Chrome work",
            ["active"] = 1,
            ["minimized"] = true,
            ["mode"] = "normal",
            ["bounds"] = new JsonObject { ["x"] = 10, ["y"] = 20, ["width"] = 800, ["height"] = 600 },
            ["tabs"] = new JsonArray(new JsonObject { ["id"] = "701", ["title"] = title, ["url"] = "https://example.com/雪?q=a,b&x=\"c\"", ["loading"] = false }),
        }),
    };

    private static JsonElement Element(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private static JsonObject FirstWindow(JsonObject snapshot) => snapshot["windows"]![0]!.AsObject();

    private static (ChromeJxa Jxa, FakeProc Proc) Bridge(string stdout, int code = 0)
    {
        var proc = new FakeProc().Answer(new Ran(code, stdout, code == 0 ? string.Empty : "not authorised"), [.. ChromeJxa.Osascript]);
        return (new ChromeJxa(proc), proc);
    }

    private static ChromeTab Tab(long window, params int[] id) => new(new RuntimeId(id), window, 0, "t", "t", true, false, true, false);

    [Fact]
    public async Task Snapshot_pipes_the_script_to_osascript_and_decodes_the_reply()
    {
        var (jxa, proc) = Bridge(Recorded().ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var snapshot = await jxa.SnapshotAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(proc.Requests);
        Assert.Equal(["/usr/bin/osascript", "-l", "JavaScript", "-"], request.Argv);
        Assert.Equal(JxaScripts.Snapshot, request.StdinText);
        Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);

        Assert.Equal((123, true), (snapshot.Pid, snapshot.Frontmost));
        var window = Assert.Single(snapshot.Windows);
        Assert.Equal((42L, 1, "Chrome work", 1L, true, "normal"), (window.Id, window.Index, window.Title, window.Active, window.Minimized, window.Mode));
        Assert.Equal(new JxaBounds(10, 20, 800, 600), window.Bounds);
        Assert.Equal(new JxaTab(701, Title, "https://example.com/雪?q=a,b&x=\"c\""), Assert.Single(window.Tabs));
        Assert.Same(window.Tabs[0], window.ActiveTab);
    }

    [Fact]
    public void No_running_chrome_is_an_empty_snapshot()
    {
        var snapshot = JxaDecode.Snapshot(Element(new JsonObject { ["pid"] = 0, ["frontmost"] = false, ["windows"] = new JsonArray() }));
        Assert.Equal((0, false), (snapshot.Pid, snapshot.Frontmost));
        Assert.Empty(snapshot.Windows);
        Assert.Null(snapshot.FrontWindow());
    }

    [Fact]
    public void Numeric_ids_decode_as_well_as_text()
    {
        var data = Recorded();
        FirstWindow(data)["id"] = 42;
        Assert.Equal(42, JxaDecode.Snapshot(Element(data)).Windows[0].Id);
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("\"1; do shell script\"")]
    [InlineData("true")]
    [InlineData("-4")]
    [InlineData("0")]
    [InlineData("\"４２\"")]
    [InlineData("\"9007199254740992\"")]
    [InlineData("9007199254740992")]
    [InlineData("42.0")]
    [InlineData("4.2e1")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Invalid_ids_fail_closed(string id)
    {
        var data = Recorded();
        FirstWindow(data)["id"] = JsonNode.Parse(id);
        var error = Assert.Throws<ChromeMacException>(() => JxaDecode.Snapshot(Element(data)));
        Assert.Equal("Invalid Chrome snapshot", error.Message);
    }

    [Fact]
    public void The_largest_exact_jxa_integer_is_an_id()
    {
        var data = Recorded();
        FirstWindow(data)["id"] = "0009007199254740991";
        Assert.Equal(JxaDecode.MaxJxaInteger, JxaDecode.Snapshot(Element(data)).Windows[0].Id);
    }

    [Theory]
    [InlineData("pid", "\"123\"")]
    [InlineData("pid", "true")]
    [InlineData("pid", "1.5")]
    [InlineData("frontmost", "1")]
    [InlineData("windows", "{}")]
    [InlineData("windows", "null")]
    public void A_snapshot_field_of_the_wrong_type_refuses_the_snapshot(string field, string value)
    {
        var data = Recorded();
        data[field] = JsonNode.Parse(value);
        Assert.Throws<ChromeMacException>(() => JxaDecode.Snapshot(Element(data)));
    }

    [Theory]
    [InlineData("index", "\"1\"")]
    [InlineData("title", "7")]
    [InlineData("active", "0")]
    [InlineData("minimized", "\"true\"")]
    [InlineData("mode", "null")]
    [InlineData("tabs", "\"none\"")]
    public void A_window_field_of_the_wrong_type_refuses_the_snapshot(string field, string value)
    {
        var data = Recorded();
        FirstWindow(data)[field] = JsonNode.Parse(value);
        Assert.Throws<ChromeMacException>(() => JxaDecode.Snapshot(Element(data)));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("frontmost")]
    [InlineData("windows")]
    public void A_missing_snapshot_field_refuses_the_snapshot(string field)
    {
        var data = Recorded();
        data.Remove(field);
        Assert.Throws<ChromeMacException>(() => JxaDecode.Snapshot(Element(data)));
    }

    [Theory]
    [InlineData("title", "null")]
    [InlineData("url", "[]")]
    [InlineData("id", "\"701x\"")]
    public void A_tab_that_does_not_fit_refuses_the_snapshot(string field, string value)
    {
        var data = Recorded();
        FirstWindow(data)["tabs"]![0]![field] = JsonNode.Parse(value);
        Assert.Throws<ChromeMacException>(() => JxaDecode.Snapshot(Element(data)));
    }

    [Fact]
    public void A_title_with_a_lone_surrogate_reads_as_python_reads_it()
    {
        var reply = JsonDocument.Parse(Recorded().ToJsonString().Replace("\"title\":\"Chrome work\"", "\"title\":\"a\\ud83db\"", StringComparison.Ordinal)).RootElement;
        Assert.Equal("a\ud83db", JxaDecode.Snapshot(reply).Windows[0].Title);
    }

    [Fact]
    public void Every_escape_reads_as_python_reads_it_beside_a_lone_surrogate()
    {
        var reply = JsonDocument.Parse("""{"status": "\ude00 \"q\" \\ \/ \b\f\n\r\t é 😀 雪"}""").RootElement;
        Assert.Equal("\ude00 \"q\" \\ / \b\f\n\r\t é 😀 雪", JxaDecode.Status(reply));
    }

    [Fact]
    public void A_reply_that_is_not_an_object_refuses_the_snapshot()
    {
        Assert.Throws<ChromeMacException>(() => JxaDecode.Snapshot(Element(new JsonArray())));
        Assert.Throws<ChromeMacException>(() => JxaDecode.Snapshot(Element(new JsonObject { ["pid"] = 1, ["frontmost"] = true, ["windows"] = new JsonArray(1) })));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[10, 20, 800, 600]")]
    [InlineData("{\"x\": 10, \"y\": 20, \"width\": 800}")]
    [InlineData("{\"x\": 10.5, \"y\": 20, \"width\": 800, \"height\": 600}")]
    public void Bounds_that_do_not_fit_read_as_none_and_keep_the_window(string bounds)
    {
        var data = Recorded();
        FirstWindow(data)["bounds"] = JsonNode.Parse(bounds);
        var window = Assert.Single(JxaDecode.Snapshot(Element(data)).Windows);
        Assert.Null(window.Bounds);
        Assert.Equal(42, window.Id);
    }

    [Fact]
    public void Missing_bounds_read_as_none()
    {
        var data = Recorded();
        FirstWindow(data).Remove("bounds");
        Assert.Null(JxaDecode.Snapshot(Element(data)).Windows[0].Bounds);
    }

    [Fact]
    public void An_active_index_past_the_tabs_has_no_active_tab()
    {
        var data = Recorded();
        FirstWindow(data)["active"] = 2;
        Assert.Null(JxaDecode.Snapshot(Element(data)).Windows[0].ActiveTab);
    }

    [Fact]
    public void The_front_window_is_the_one_lowest_index()
    {
        var data = Recorded();
        var windows = data["windows"]!.AsArray();
        var hidden = FirstWindow(data).DeepClone().AsObject();
        hidden["id"] = "43";
        hidden["index"] = 3;
        windows.Add(hidden);
        FirstWindow(data)["index"] = 2;
        Assert.Equal(42, JxaDecode.Snapshot(Element(data)).FrontWindow()!.Id);

        hidden["index"] = 2;
        Assert.Null(JxaDecode.Snapshot(Element(data)).FrontWindow());
    }

    [Fact]
    public async Task A_failed_run_names_the_automation_permission()
    {
        var (jxa, _) = Bridge(string.Empty, code: 1);
        var error = await Assert.ThrowsAsync<ChromeMacException>(() => jxa.SnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Automation permission", error.Message, StringComparison.Ordinal);
        Assert.IsType<ProcException>(error.InnerException);
    }

    [Fact]
    public async Task A_timed_out_run_names_the_automation_permission()
    {
        var (jxa, _) = Bridge(string.Empty, code: Ran.TimedOut);
        var error = await Assert.ThrowsAsync<ChromeMacException>(() => jxa.RunAsync("query", TestContext.Current.CancellationToken));
        Assert.Contains("Automation permission", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"pid\": 1")]
    public async Task A_reply_that_is_not_json_names_the_automation_permission(string stdout)
    {
        var (jxa, _) = Bridge(stdout);
        var error = await Assert.ThrowsAsync<ChromeMacException>(() => jxa.RunAsync("query", TestContext.Current.CancellationToken));
        Assert.Contains("Automation permission", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_preserves_strings_exactly()
    {
        var (jxa, proc) = Bridge("{\"title\":\"雪, \\\"quoted\\\"\\nline\",\"url\":\"https://example.test/?a=1,2\"}\n");
        var reply = await jxa.RunAsync("query", TestContext.Current.CancellationToken);
        Assert.Equal("query", proc.Requests[0].StdinText);
        Assert.Equal("雪, \"quoted\"\nline", reply.GetProperty("title").GetString());
        Assert.Equal("https://example.test/?a=1,2", reply.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("{\"pid\": 501}", 501)]
    [InlineData("{\"pid\": 0}", null)]
    public async Task Frontmost_pid_is_null_when_no_application_is_frontmost(string stdout, int? pid)
    {
        var (jxa, proc) = Bridge(stdout);
        Assert.Equal(pid, await jxa.FrontmostPidAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JxaScripts.Frontmost, proc.Requests[0].StdinText);
    }

    [Fact]
    public async Task A_frontmost_reply_that_does_not_fit_is_refused()
    {
        var (jxa, _) = Bridge("{\"pid\": \"501\"}");
        var error = await Assert.ThrowsAsync<ChromeMacException>(() => jxa.FrontmostPidAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Invalid Chrome frontmost application", error.Message);
    }

    [Fact]
    public async Task Act_returns_the_status_the_script_replied_with()
    {
        var (jxa, proc) = Bridge("{\"status\": \"invoked\"}");
        Assert.Equal("invoked", await jxa.ActAsync("script", TestContext.Current.CancellationToken));
        Assert.Equal("script", proc.Requests[0].StdinText);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\": 1}")]
    [InlineData("\"invoked\"")]
    public void A_status_that_does_not_fit_is_refused(string reply)
    {
        var error = Assert.Throws<ChromeMacException>(() => JxaDecode.Status(JsonDocument.Parse(reply).RootElement));
        Assert.Equal("Invalid Chrome action result", error.Message);
    }

    [Fact]
    public void Action_ids_come_from_the_window_and_the_single_tab_id()
    {
        Assert.Equal(new ActionIds(42, 701), ActionIds.Of(Tab(42, 701)));
    }

    [Fact]
    public void A_tab_identity_of_more_than_one_part_is_refused()
    {
        var error = Assert.Throws<ChromeMacException>(() => ActionIds.Of(Tab(42, 42, 1, 0)));
        Assert.Equal("Invalid Chrome tab identity", error.Message);
    }

    [Theory]
    [InlineData(0L, 701)]
    [InlineData(42L, 0)]
    [InlineData(42L, -1)]
    [InlineData(9007199254740992L, 701)]
    public void Ids_outside_jxa_range_are_refused(long window, int tab)
    {
        var error = Assert.Throws<ChromeMacException>(() => ActionIds.Of(Tab(window, tab)));
        Assert.Equal("Invalid Chrome action ID", error.Message);
    }

    [Fact]
    public void A_tab_action_splices_the_ids_before_the_action()
    {
        var script = JxaScripts.TabAction(Tab(42, 701), "// __WINDOW_ID__ __TAB_ID__ stays");
        Assert.Contains("const windowID = 42, tabID = 701;", script, StringComparison.Ordinal);
        Assert.Contains("    // __WINDOW_ID__ __TAB_ID__ stays\n}\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__ACTION__", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_close_action_carries_the_expected_page_as_ascii_json()
    {
        var action = JxaScripts.CloseAction(Title, "https://example.com/雪?x=\"c\"\u2028");
        Assert.StartsWith(
            "const expected = {\"title\": \"Notes, \\\"\\u96ea\\\"\\nline\", \"url\": \"https://example.com/\\u96ea?x=\\\"c\\\"\\u2028\"};\n\n",
            action,
            StringComparison.Ordinal);
        Assert.Contains("target.close();", action, StringComparison.Ordinal);
        Assert.True(action.All(char.IsAscii));
    }

    [Fact]
    public void A_close_action_with_placeholder_tokens_in_the_page_stays_data()
    {
        var script = JxaScripts.TabAction(Tab(42, 701), JxaScripts.CloseAction("__TAB_ID__ __ACTION__", "about:blank"));
        Assert.Contains("{\"title\": \"__TAB_ID__ __ACTION__\", \"url\": \"about:blank\"}", script, StringComparison.Ordinal);
        Assert.Contains("tabID = 701;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Activate_names_the_window()
    {
        var script = JxaScripts.Activate(42);
        Assert.Contains("id = 42;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__ID__", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(9007199254740992L)]
    public void Activate_refuses_an_id_outside_jxa_range(long handle)
    {
        var error = Assert.Throws<ChromeMacException>(() => JxaScripts.Activate(handle));
        Assert.Equal("Invalid Chrome window identity", error.Message);
    }

    [Fact]
    public void Restore_carries_its_state_as_json()
    {
        Assert.Contains("const state = {\"target\": 42, \"front\": 43, \"minimized\": true};", JxaScripts.Restore(42, 43, minimized: true), StringComparison.Ordinal);
        Assert.Contains("\"minimized\": false}", JxaScripts.Restore(42, 43, minimized: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Scripts_keep_python_layout()
    {
        Assert.StartsWith("\nObjC.import('AppKit');\nfunction run() {\n", JxaScripts.Snapshot, StringComparison.Ordinal);
        Assert.EndsWith("\n}\n", JxaScripts.Snapshot, StringComparison.Ordinal);
        Assert.StartsWith("\n    window.activeTabIndex", JxaScripts.SelectAction, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', JxaScripts.Snapshot);
    }
}
