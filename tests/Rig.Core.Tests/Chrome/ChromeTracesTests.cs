using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Chrome;

namespace Rig.Core.Tests.Chrome;

/// <summary>
/// Organise-run traces: retention, reading spans back, and the runs, tree and summary reports. The rendered lines are
/// the Python reports' output for the same fixture. Replaces the retention and report halves of
/// <c>test_chrome_traces</c>.
/// </summary>
public sealed class ChromeTracesTests
{
    private const long Second = 1_000_000_000;
    private const long Base = 1_790_000_000 * Second;
    private const string Wait = "the machine was used 3s ago (waits for 1 min idle)";
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static readonly double[] OddDurations = [1.0, 3.0, 2.0];
    private static readonly TimeZoneInfo Bst = TimeZoneInfo.CreateCustomTimeZone("bst", TimeSpan.FromHours(1), "bst", "bst");

    // -- retention ------------------------------------------------------------------------------------------------

    private static void DayFiles(string directory, params string[] days)
    {
        _ = Directory.CreateDirectory(directory);
        foreach (var day in days)
        {
            File.WriteAllText(Path.Join(directory, $"{day}.ndjson"), "{}\n");
        }
    }

    private static string[] Names(IEnumerable<string> paths) => [.. paths.Select(Path.GetFileName).Select(name => name!)];

    [Fact]
    public void Files_more_than_thirty_days_old_are_deleted()
    {
        using var temp = new TempDir();
        DayFiles(temp.Path, "2026-08-01", "2026-09-05", "2026-09-06", "2026-10-06");
        File.WriteAllText(temp.File("notes.txt"), "not a trace");
        File.WriteAllText(temp.File("2026-13-01.ndjson"), "not a day");

        var removed = ChromeTraces.Prune(temp.Path, Today);

        Assert.Equal(["2026-08-01.ndjson", "2026-09-05.ndjson"], Names(removed));
        Assert.Equal(
            ["2026-09-06.ndjson", "2026-10-06.ndjson", "2026-13-01.ndjson", "notes.txt"],
            Names(Directory.EnumerateFiles(temp.Path)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_dry_run_lists_without_deleting()
    {
        using var temp = new TempDir();
        DayFiles(temp.Path, "2026-01-01", "2026-10-01");

        Assert.Equal(["2026-01-01.ndjson"], Names(ChromeTraces.Prune(temp.Path, Today, dryRun: true)));
        Assert.Equal(2, Directory.EnumerateFiles(temp.Path).Count());
    }

    [Fact]
    public void A_missing_folder_has_no_files()
    {
        using var temp = new TempDir();

        Assert.Empty(ChromeTraces.TraceFiles(Path.Join(temp.Path, "gone")));
    }

    [Fact]
    public void A_file_that_cannot_be_deleted_is_left()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file that is held open.");
        using var temp = new TempDir();
        DayFiles(temp.Path, "2026-01-01");
        using var held = new FileStream(temp.File("2026-01-01.ndjson"), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Empty(ChromeTraces.Prune(temp.Path, Today));
    }

    [Fact]
    public void The_daily_prune_runs_once_a_day()
    {
        using var temp = new TempDir();
        DayFiles(temp.Path, "2026-01-01");

        Assert.Equal(["2026-01-01.ndjson"], Names(ChromeTraces.PruneDaily(temp.Path, Today)!));
        DayFiles(temp.Path, "2026-01-02");
        Assert.Null(ChromeTraces.PruneDaily(temp.Path, Today));
        Assert.True(File.Exists(temp.File("2026-01-02.ndjson")));
        Assert.Equal(["2026-01-02.ndjson"], Names(ChromeTraces.PruneDaily(temp.Path, Today.AddDays(1))!));
    }

    [Fact]
    public void The_daily_prune_of_a_missing_folder_creates_it()
    {
        using var temp = new TempDir();

        Assert.Empty(ChromeTraces.PruneDaily(Path.Join(temp.Path, "new"), Today)!);
    }

    [Fact]
    public void A_daily_prune_that_cannot_write_its_marker_does_nothing()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.File("taken"), "a file where the folder should be");

        Assert.Null(ChromeTraces.PruneDaily(Path.Join(temp.Path, "taken"), Today));
    }

    [Fact]
    public void Traces_live_beside_the_scheduled_run()
    {
        Assert.Equal(
            Path.Join("L", "shared-utilities", "chrome-tabs", "traces"),
            ChromeTraces.TracesDir(name => name == "LOCALAPPDATA" ? "L" : null, "H"));
    }

    // -- the fixture ----------------------------------------------------------------------------------------------

    private static JsonObject Value(object value) =>
        value switch
        {
            bool flag => new JsonObject { ["boolValue"] = flag },
            int whole => new JsonObject { ["intValue"] = whole.ToString(CultureInfo.InvariantCulture) },
            double real => new JsonObject { ["doubleValue"] = real },
            _ => new JsonObject { ["stringValue"] = (string)value },
        };

    private static JsonArray Attributes(params (string Key, object Value)[] attributes) =>
        [.. attributes.Select(pair => (JsonNode)new JsonObject { ["key"] = pair.Key, ["value"] = Value(pair.Value) })];

    private static string Nanos(double seconds) => (Base + (long)(seconds * Second)).ToString(CultureInfo.InvariantCulture);

    private static JsonObject Raw(
        string name,
        string spanId,
        string parent = "",
        double start = 0,
        double end = 1,
        string trace = "t1",
        string error = "",
        (string Name, double At, (string, object)[] Detail)[]? events = null,
        params (string, object)[] attributes)
    {
        var raw = new JsonObject
        {
            ["traceId"] = trace,
            ["spanId"] = spanId,
            ["parentSpanId"] = parent,
            ["name"] = name,
            ["startTimeUnixNano"] = Nanos(start),
            ["endTimeUnixNano"] = Nanos(end),
            ["status"] = error.Length > 0 ? new JsonObject { ["code"] = 2, ["message"] = error } : new JsonObject { ["code"] = 1 },
            ["attributes"] = Attributes(attributes),
        };
        if (events is not null)
        {
            raw["events"] = new JsonArray(
            [
                .. events.Select(spanEvent => (JsonNode)new JsonObject
                {
                    ["name"] = spanEvent.Name,
                    ["timeUnixNano"] = Nanos(spanEvent.At),
                    ["attributes"] = Attributes(spanEvent.Detail),
                }),
            ]);
        }

        return raw;
    }

    private static string Envelope(params JsonNode[] spans) =>
        new JsonObject
        {
            ["resourceSpans"] = new JsonArray(
                new JsonObject
                {
                    ["scopeSpans"] = new JsonArray(
                        new JsonObject { ["scope"] = new JsonObject { ["name"] = ChromeTraces.Scope }, ["spans"] = new JsonArray(spans) }),
                }),
        }.ToJsonString();

    // Three runs: grouped after a wait, deferred, and one killed mid-run (incomplete).
    private static string[] FixtureLines()
    {
        (string, double, (string, object)[])[] checks =
        [
            ("check", 2, [("blocked", true), ("reason", "the machine was used 12s ago (waits for 1 min idle)")]),
            ("check", 50, [("blocked", true), ("reason", Wait)]),
            ("check", 110, [("blocked", false)]),
        ];
        var killed = Raw(ChromeTraces.RunSpan, "cccc000000000003", trace: "t3", start: 7200, end: 7300, attributes: [("chrome.trigger", "manual")]);
        killed["attributes"]!.AsArray().Add(new JsonObject { ["key"] = "span.incomplete", ["value"] = Value(true) });
        return
        [
            Envelope(
                Raw(
                    ChromeTraces.RunSpan,
                    "aaaa000000000001",
                    end: 130,
                    attributes:
                    [
                        ("chrome.trigger", "scheduled"), ("run.outcome", "ok"), ("group.outcome", "grouped"), ("group.planned", 4),
                        ("group.moved", 4), ("tabs.closed", 3), ("wait.seconds", 108.0), ("wait.checks", 3), ("wait.last_block", Wait),
                    ]),
                Raw("chrome_tabs.read_windows", "r1", "aaaa000000000001", 0, 1, attributes: [("chrome.windows", 2)]),
                Raw("chrome_tabs.wait_for_idle", "w1", "aaaa000000000001", 2, 110, events: checks),
                Raw("chrome_tabs.group", "g1", "aaaa000000000001", 110, 130),
                Raw("chrome_tabs.group_window", "gw1", "g1", 111, 129, attributes: [("window.number", 1)])),
            "not json",
            Envelope(
                Raw(
                    ChromeTraces.RunSpan,
                    "bbbb000000000002",
                    trace: "t2",
                    start: 3600,
                    end: 3610,
                    attributes:
                    [
                        ("chrome.trigger", "scheduled"), ("run.outcome", "ok"), ("group.outcome", "deferred"), ("tabs.closed", 1),
                        ("wait.seconds", 0.0), ("wait.checks", 1), ("wait.last_block", "the session is locked"),
                    ]),
                Raw(
                    "chrome_tabs.wait_for_idle",
                    "w2",
                    "bbbb000000000002",
                    3601,
                    3601,
                    "t2",
                    events: [("check", 3601, [("blocked", true), ("reason", "the session is locked")])])),
            Envelope(killed),
        ];
    }

    private static IReadOnlyList<TraceRun> FixtureRuns() => ChromeTraces.Runs(ChromeTraces.ParseLines(FixtureLines()));

    // -- reading spans back ---------------------------------------------------------------------------------------

    [Fact]
    public void Runs_are_rooted_and_ordered_with_their_descendants()
    {
        var found = FixtureRuns();

        Assert.Equal(["aaaa0000", "bbbb0000", "cccc0000"], found.Select(run => run.Id));
        Assert.Equal(
            ["chrome_tabs.group", "chrome_tabs.group_window", "chrome_tabs.read_windows", "chrome_tabs.wait_for_idle"],
            found[0].Spans.Select(span => span.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["ok", "ok", "incomplete"], found.Select(run => run.Outcome));
        Assert.Equal(["grouped", "deferred", "-"], found.Select(run => run.Grouping));
        Assert.Equal(["the machine was used 12s ago (waits for 1 min idle)", Wait], found[0].Blocks);
    }

    [Fact]
    public void The_complete_copy_of_a_span_replaces_its_snapshot()
    {
        var complete = Envelope(Raw(ChromeTraces.RunSpan, "dddd000000000004", end: 5, attributes: [("run.outcome", "ok")]));
        var snapshot = Envelope(Raw(ChromeTraces.RunSpan, "dddd000000000004", end: 2, attributes: [("span.incomplete", true)]));
        foreach (var lines in new[] { new[] { snapshot, complete }, [complete, snapshot] })
        {
            var run = Assert.Single(ChromeTraces.Runs(ChromeTraces.ParseLines(lines)));
            Assert.Equal("ok", run.Outcome);
            Assert.Equal(5, run.Root.Seconds);
        }
    }

    [Fact]
    public void A_failed_run_carries_its_error()
    {
        var run = Assert.Single(ChromeTraces.Runs(ChromeTraces.ParseLines([Envelope(Raw(ChromeTraces.RunSpan, "eeee000000000005", error: "RuntimeError: no chrome"))])));

        Assert.Equal("error", run.Outcome);
        Assert.Equal("RuntimeError: no chrome", ChromeTraces.RunJson(run, Bst)["error"]!.GetValue<string>());
        Assert.EndsWith(" [error: RuntimeError: no chrome]", ChromeTraces.RenderTree(run)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Attribute_values_decode_as_python_read_them()
    {
        var attributes = new JsonArray(
            new JsonObject { ["key"] = "list", ["value"] = JsonNode.Parse("""{"arrayValue":{"values":[{"stringValue":"a"},{"intValue":"2"},{"doubleValue":1.5},{"boolValue":true}]}}""") },
            new JsonObject { ["key"] = "empty", ["value"] = JsonNode.Parse("""{"arrayValue":{}}""") },
            new JsonObject { ["key"] = "badlist", ["value"] = JsonNode.Parse("""{"arrayValue":{"values":[7]},"stringValue":"s"}""") },
            new JsonObject { ["key"] = "notlist", ["value"] = JsonNode.Parse("""{"arrayValue":3,"intValue":" -4 "}""") },
            new JsonObject { ["key"] = "wrong", ["value"] = JsonNode.Parse("""{"stringValue":1,"boolValue":"x","intValue":true}""") },
            new JsonObject { ["key"] = "whole", ["value"] = JsonNode.Parse("""{"intValue":5.0}""") },
            new JsonObject { ["key"] = "frac", ["value"] = JsonNode.Parse("""{"intValue":5.5,"doubleValue":2}""") },
            new JsonObject { ["key"] = "falsy", ["value"] = JsonNode.Parse("""{"intValue":false}""") },
            new JsonObject { ["key"] = "nothing", ["value"] = JsonNode.Parse("""{"intValue":"x","doubleValue":"y"}""") },
            new JsonObject { ["key"] = "nullvalue", ["value"] = JsonNode.Parse("""{"intValue":null}""") },
            new JsonObject { ["key"] = "huge", ["value"] = JsonNode.Parse("""{"intValue":1e30}""") },
            new JsonObject { ["key"] = "novalue" });
        var raw = Raw("s", "f1");
        raw["attributes"] = attributes;

        var span = Assert.Single(ChromeTraces.ParseLines([Envelope(raw)]));

        Assert.Equal(new object?[] { "a", 2L, 1.5, true }, (List<object?>)span.Attributes["list"]!);
        Assert.Empty((List<object?>)span.Attributes["empty"]!);
        Assert.Equal("s", span.Attributes["badlist"]);
        Assert.Equal(-4L, span.Attributes["notlist"]);
        Assert.Equal(1L, span.Attributes["wrong"]);
        Assert.Equal(5L, span.Attributes["whole"]);
        Assert.Equal(2.0, span.Attributes["frac"]);
        Assert.Equal(0L, span.Attributes["falsy"]);
        Assert.Null(span.Attributes["nothing"]);
        Assert.Null(span.Attributes["nullvalue"]);
        Assert.Null(span.Attributes["huge"]);
        Assert.Null(span.Attributes["novalue"]);
    }

    [Theory]
    [InlineData("status", """ "bad" """)]
    [InlineData("status", """ null """)]
    [InlineData("attributes", """ {} """)]
    [InlineData("attributes", """ [1] """)]
    [InlineData("attributes", """ [{"key":1}] """)]
    [InlineData("attributes", """ [{"key":"k","value":1}] """)]
    [InlineData("events", """ [1] """)]
    [InlineData("events", """ [{"name":"e","timeUnixNano":"x"}] """)]
    [InlineData("name", """ 1 """)]
    [InlineData("name", """ null """)]
    [InlineData("startTimeUnixNano", """ "soon" """)]
    public void A_malformed_span_is_skipped(string field, string json)
    {
        var bad = Raw("bad", "b1");
        bad[field] = JsonNode.Parse(json);

        Assert.Equal(["good"], ChromeTraces.ParseLines([Envelope(bad, Raw("good", "g1"))]).Select(span => span.Name));
    }

    [Fact]
    public void Missing_optional_fields_take_their_defaults()
    {
        var raw = JsonNode.Parse("""{"traceId":"t","spanId":"s","events":[{}],"status":{"code":2}}""")!;

        var span = Assert.Single(ChromeTraces.ParseLines([Envelope(raw)]));

        Assert.Equal(("", "", 0L, 0L, ""), (span.ParentId, span.Name, span.StartNs, span.EndNs, span.Error));
        Assert.Empty(span.Attributes);
        Assert.Equal(new TraceEvent("", 0, new Dictionary<string, object?>()).Name, Assert.Single(span.Events).Name);
    }

    [Theory]
    [InlineData("""{"code":2,"message":null}""", "None")]
    [InlineData("""{"code":2,"message":true}""", "True")]
    [InlineData("""{"code":2,"message":false}""", "False")]
    [InlineData("""{"code":2,"message":3}""", "3")]
    [InlineData("""{"code":2.0,"message":"m"}""", "m")]
    [InlineData("""{"code":"2","message":"m"}""", "")]
    public void The_error_is_the_message_of_an_error_status(string status, string error)
    {
        var raw = Raw("s", "s1");
        raw["status"] = JsonNode.Parse(status);

        Assert.Equal(error, Assert.Single(ChromeTraces.ParseLines([Envelope(raw)])).Error);
    }

    [Fact]
    public void Spans_load_from_the_window_of_days()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.File("2026-10-06.ndjson"), Envelope(Raw(ChromeTraces.RunSpan, "new0000000000001")) + "\r\n");
        File.WriteAllText(temp.File("2026-09-01.ndjson"), Envelope(Raw(ChromeTraces.RunSpan, "old0000000000001")) + "\n");

        Assert.Equal(["new0000000000001"], ChromeTraces.LoadSpans(temp.Path, Today, 7).Select(span => span.SpanId));
        Assert.Equal(2, ChromeTraces.LoadSpans(temp.Path, Today).Count);
    }

    [Fact]
    public void An_unreadable_day_file_is_skipped()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.File("2026-10-06.ndjson"), Envelope(Raw(ChromeTraces.RunSpan, "new0000000000001")));
        using var held = new FileStream(temp.File("2026-10-06.ndjson"), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Empty(ChromeTraces.LoadSpans(temp.Path, Today));
    }

    [Fact]
    public void Find_run_defaults_to_the_latest()
    {
        var found = FixtureRuns();

        Assert.Same(found[^1], ChromeTraces.FindRun(found, null));
        Assert.Same(found[1], ChromeTraces.FindRun(found, "BBBB"));
        Assert.Null(ChromeTraces.FindRun(found, "ffff"));
        Assert.Null(ChromeTraces.FindRun([], null));
    }

    [Fact]
    public void A_run_without_its_own_wait_seconds_falls_back_to_the_wait_span()
    {
        var lines = new[]
        {
            Envelope(
                Raw(ChromeTraces.RunSpan, "ffff000000000006", end: 9, attributes: [("chrome.unattended", true)]),
                Raw("chrome_tabs.wait_for_idle", "w6", "ffff000000000006", 1, 4)),
        };

        var run = Assert.Single(ChromeTraces.Runs(ChromeTraces.ParseLines(lines)));

        Assert.Equal(3, run.WaitedSeconds);
        Assert.Equal(0, run.Checks);
        Assert.True(run.Unattended);
        Assert.Null(Assert.Single(ChromeTraces.Runs(ChromeTraces.ParseLines([Envelope(Raw(ChromeTraces.RunSpan, "f7"))]))).WaitedSeconds);
    }

    // -- reports --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_tree_matches_the_python_report()
    {
        var found = FixtureRuns();

        Assert.Equal(
            [
                "chrome_tabs.organise  2.2m at +0.0s",
                "    chrome.trigger=scheduled run.outcome=ok group.outcome=grouped group.planned=4 group.moved=4 tabs.closed=3 " +
                $"wait.seconds=108.0 wait.checks=3 wait.last_block={Wait}",
                "  chrome_tabs.read_windows  1.0s at +0.0s",
                "      chrome.windows=2",
                "  chrome_tabs.wait_for_idle  108.0s at +2.0s",
                "      - +2.0s check blocked=True reason=the machine was used 12s ago (waits for 1 min idle)",
                $"      - +50.0s check blocked=True reason={Wait}",
                "      - +110.0s check blocked=False",
                "  chrome_tabs.group  20.0s at +110.0s",
                "    chrome_tabs.group_window  18.0s at +111.0s",
                "        window.number=1",
            ],
            ChromeTraces.RenderTree(found[0]));
        Assert.Equal(["chrome_tabs.organise  100.0s at +0.0s [incomplete]", "    chrome.trigger=manual"], ChromeTraces.RenderTree(found[2]));
    }

    [Fact]
    public void Lists_and_odd_values_render_as_python_printed_them()
    {
        var raw = Raw("s", "a1", end: 0.25);
        raw["attributes"] = JsonNode.Parse("""[{"key":"l","value":{"arrayValue":{"values":[{"stringValue":"a'b"},{"intValue":"1"},{"doubleValue":0.1}]}}},{"key":"n"}]""");
        var run = new TraceRun(Assert.Single(ChromeTraces.ParseLines([Envelope(raw)])), []);

        Assert.Equal(["s  250ms at +0.0s", "    l=[\"a'b\", 1, 0.1] n=None"], ChromeTraces.RenderTree(run));
    }

    [Fact]
    public void The_runs_list_matches_the_python_report()
    {
        Assert.Equal(
            "run       started             took  trigger    outcome     closed  grouping         waited  checks  last block",
            ChromeTraces.RunsHeader);
        Assert.Equal(
            [
                "cccc0000  2026-09-21 17:13  100.0s  manual     incomplete       -  -                     -       -  ",
                "bbbb0000  2026-09-21 16:13   10.0s  scheduled  ok               1  deferred            0ms       1  the session is locked",
                $"aaaa0000  2026-09-21 15:13    2.2m  scheduled  ok               3  grouped 4/4      108.0s       3  {Wait}",
            ],
            ChromeTraces.RenderRuns(FixtureRuns(), Bst));
    }

    [Fact]
    public void A_run_as_json_matches_the_python_report()
    {
        var json = ChromeTraces.RunJson(FixtureRuns()[0], Bst);

        var expected = JsonNode.Parse(
            """
            {
              "id": "aaaa0000", "trace_id": "t1", "span_id": "aaaa000000000001", "started": "2026-09-21T15:13:20+01:00",
              "seconds": 130, "trigger": "scheduled", "unattended": null, "outcome": "ok", "grouping": "grouped",
              "tabs_closed": 3, "group_planned": 4, "group_moved": 4, "waited_seconds": 108, "checks": 3,
              "last_block": "the machine was used 3s ago (waits for 1 min idle)",
              "blocks": ["the machine was used 12s ago (waits for 1 min idle)", "the machine was used 3s ago (waits for 1 min idle)"],
              "error": null
            }
            """);

        Assert.Equal(expected!.ToJsonString(), json.ToJsonString());
    }

    [Fact]
    public void The_summary_matches_the_python_report()
    {
        var summary = ChromeTraces.Summarise(FixtureRuns());

        Assert.Equal((3, 2, 2, 1), (summary.Runs, summary.Completed, summary.Ok, summary.Incomplete));
        Assert.Equal(1.0, summary.SuccessRate);
        Assert.Equal([108.0, 0.0], summary.Waits);
        Assert.Equal(4, summary.Closed);
        Assert.Equal(
            [
                "Last 7 days: 3 runs, 2 completed, 1 incomplete",
                "  success rate        100% (2/2)",
                "  grouping            grouped 1, deferred 1, - 1",
                "  waited for idle     2 runs, median 0ms, p90 108.0s",
                "  run duration        median 70.0s, p90 2.2m",
                "  tabs closed         4",
                "  commonest blocks",
                "       2  the machine was used Ns ago (waits for N min idle)",
                "       1  the session is locked",
            ],
            ChromeTraces.RenderSummary(summary, 7));
    }

    [Fact]
    public void An_empty_summary_shows_dashes()
    {
        var summary = ChromeTraces.Summarise([]);

        Assert.Null(summary.SuccessRate);
        Assert.Equal(
            [
                "Last 1 days: 0 runs, 0 completed, 0 incomplete",
                "  success rate        - (0/0)",
                "  grouping            -",
                "  waited for idle     0 runs, median -, p90 -",
                "  run duration        median -, p90 -",
                "  tabs closed         0",
            ],
            ChromeTraces.RenderSummary(summary, 1));
    }

    [Fact]
    public void An_odd_count_of_durations_takes_the_middle_one()
    {
        var lines = OddDurations.Select((seconds, index) =>
            Envelope(Raw(ChromeTraces.RunSpan, $"r{index}", start: index * 10, end: (index * 10) + seconds, trace: $"t{index}"))).ToList();

        Assert.Equal([1.0, 3.0, 2.0], ChromeTraces.Summarise(ChromeTraces.Runs(ChromeTraces.ParseLines(lines))).Durations);
        Assert.Contains(
            "  run duration        median 2.0s, p90 3.0s",
            ChromeTraces.RenderSummary(ChromeTraces.Summarise(ChromeTraces.Runs(ChromeTraces.ParseLines(lines))), 1));
    }

    [Fact]
    public void A_stopped_run_shows_how_far_grouping_got()
    {
        var lines = new[]
        {
            Envelope(Raw(ChromeTraces.RunSpan, "s1", attributes: [("group.outcome", "stopped"), ("group.planned", 5)])),
        };

        Assert.Contains("stopped None/5", ChromeTraces.RenderRuns(ChromeTraces.Runs(ChromeTraces.ParseLines(lines)), Bst)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_and_odd_root_attributes_fall_back()
    {
        var raw = Raw(
            ChromeTraces.RunSpan,
            "h1",
            attributes: [("run.outcome", ""), ("group.outcome", ""), ("wait.seconds", 7), ("wait.checks", 2)]);
        raw.Remove("status");
        var odd = Raw(ChromeTraces.RunSpan, "h2", start: 5, trace: "t9", attributes: [("wait.seconds", "soon"), ("wait.checks", 1)]);

        var found = ChromeTraces.Runs(ChromeTraces.ParseLines([Envelope(raw, odd)]));

        Assert.Equal(("ok", "-", 7.0), (found[0].Outcome, found[0].Grouping, found[0].WaitedSeconds));
        Assert.Null(found[1].WaitedSeconds);
        Assert.Equal([7.0], ChromeTraces.Summarise(found).Waits);
    }

    [Fact]
    public void A_parent_is_required_to_list_children()
    {
        Assert.Throws<ArgumentNullException>(() => FixtureRuns()[0].Children(null!));
    }
}
