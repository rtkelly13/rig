using System.CommandLine;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Chrome;
using Rig.Core.Chrome;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workspace;

namespace Rig.Cli.Tests.Chrome;

/// <summary>
/// <c>rig chrome-tabs</c>. <c>traces</c> runs over <see cref="ChromeTraces"/>, which its own tests hold to Python's
/// reports; <c>list</c>, <c>rank</c> and <c>prune</c> over a fake session; <c>install</c> and <c>uninstall</c> over a temp home. These check what the command adds: the files it picks, the lines,
/// the JSON and its key order, the exit codes, and which lines stay with the Python.
/// </summary>
public sealed class ChromeTabsCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 23, 30, 0, TimeSpan.Zero);
    private static readonly long Base = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000;
    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();

    public void Dispose()
    {
        _out.Dispose();
        _temp.Dispose();
    }

    private string Traces => Path.Join(_temp.Path, "shared-utilities", "chrome-tabs", "traces");

    private async Task<int> Run(params string[] args) =>
        await new ChromeTabsCommand(name => name == "LOCALAPPDATA" ? _temp.Path : null, "/home", new FixedTime(Now), TimeZoneInfo.Utc, _out)
            .Build()
            .Parse(["traces", .. args])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

    private string[] Lines => _out.ToString().Split('\n')[..^1];

    private static string Span(string name, string id, string parent, double start, double end, params (string Key, object Value)[] attributes) =>
        new JsonObject
        {
            ["traceId"] = "t" + id,
            ["spanId"] = id,
            ["parentSpanId"] = parent,
            ["name"] = name,
            ["startTimeUnixNano"] = Nanos(start),
            ["endTimeUnixNano"] = Nanos(end),
            ["status"] = new JsonObject { ["code"] = 1 },
            ["attributes"] = new JsonArray(
            [
                .. attributes.Select(pair => (JsonNode)new JsonObject
                {
                    ["key"] = pair.Key,
                    ["value"] = pair.Value switch
                    {
                        int whole => new JsonObject { ["intValue"] = whole.ToString(CultureInfo.InvariantCulture) },
                        double real => new JsonObject { ["doubleValue"] = real },
                        _ => new JsonObject { ["stringValue"] = (string)pair.Value },
                    },
                }),
            ]),
        }.ToJsonString();

    private static string Nanos(double seconds) => (Base + (long)(seconds * 1e9)).ToString(CultureInfo.InvariantCulture);

    private static string Envelope(params string[] spans) =>
        $$"""{"resourceSpans":[{"scopeSpans":[{"scope":{"name":"{{ChromeTraces.Scope}}"},"spans":[{{string.Join(',', spans)}}]}]}]}""";

    // Two runs today: an older one that grouped, and a newer one that deferred after a wait, its wait's block named "café".
    private void TwoRuns(string? directory = null)
    {
        var folder = directory ?? Traces;
        Directory.CreateDirectory(folder);
        File.WriteAllLines(
            Path.Join(folder, "2026-03-04.ndjson"),
            [
                Envelope(
                    Span(ChromeTraces.RunSpan, "aaaa000000000001", string.Empty, 0, 30, ("chrome.trigger", "scheduled"), ("run.outcome", "ok"), ("group.outcome", "grouped"), ("tabs.closed", 3)),
                    Span("chrome_tabs.group", "g1", "aaaa000000000001", 10, 30)),
                Envelope(
                    Span(
                        ChromeTraces.RunSpan,
                        "bbbb000000000002",
                        string.Empty,
                        3600,
                        3700,
                        ("chrome.trigger", "manual"),
                        ("run.outcome", "ok"),
                        ("group.outcome", "deferred"),
                        ("tabs.closed", 1),
                        ("wait.seconds", 90.0),
                        ("wait.checks", 2),
                        ("wait.last_block", "café"))),
            ]);
    }

    [Fact]
    public async Task Runs_lists_the_newest_first_under_the_header()
    {
        TwoRuns();

        Assert.Equal(0, await Run("runs"));

        Assert.Equal(ChromeTraces.RunsHeader, Lines[0]);
        Assert.Equal(3, Lines.Length);
        Assert.StartsWith("bbbb0000", Lines[1], StringComparison.Ordinal);
        Assert.StartsWith("aaaa0000", Lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runs_keeps_only_the_most_recent_limit()
    {
        TwoRuns();

        Assert.Equal(0, await Run("runs", "--limit", "1"));

        Assert.Equal(2, Lines.Length);
        Assert.StartsWith("bbbb0000", Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runs_as_json_is_newest_first_and_keeps_non_ascii()
    {
        TwoRuns();

        Assert.Equal(0, await Run("runs", "--json"));

        var text = _out.ToString();
        Assert.Contains("café", text, StringComparison.Ordinal);
        Assert.StartsWith("[\n  {\n    \"id\": ", text, StringComparison.Ordinal);
        var rows = JsonNode.Parse(text)!.AsArray();
        Assert.Equal(["bbbb0000", "aaaa0000"], rows.Select(row => row!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Runs_says_so_when_there_are_none_and_json_prints_an_empty_list()
    {
        Assert.Equal(0, await Run("runs", "--days", "3"));
        Assert.Equal(["No organise runs in the last 3 days."], Lines);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run("runs", "--json"));
        Assert.Equal(["[]"], Lines);
    }

    [Fact]
    public async Task A_dir_reads_another_folder()
    {
        var elsewhere = _temp.Folder("elsewhere");
        TwoRuns(elsewhere);

        Assert.Equal(0, await Run("runs", "--dir", elsewhere));

        Assert.Equal(3, Lines.Length);
    }

    [Fact]
    public async Task Show_prints_the_latest_run_or_the_one_a_prefix_names()
    {
        TwoRuns();

        Assert.Equal(0, await Run("show"));
        Assert.Equal(ChromeTraces.RenderTree(Load()[1]), Lines);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run("show", "aaaa"));
        Assert.Equal(ChromeTraces.RenderTree(Load()[0]), Lines);
    }

    [Fact]
    public async Task Show_exits_one_when_no_run_matches()
    {
        TwoRuns();

        Assert.Equal(ChromeTabsCommand.NoRunExit, await Run("show", "zzzz", "--days", "2"));
        Assert.Equal(["No organise run zzzz in the last 2 days."], Lines);

        _out.GetStringBuilder().Clear();
        Assert.Equal(ChromeTabsCommand.NoRunExit, await Run("show", "--dir", _temp.Folder("empty")));
        Assert.Equal(["No organise run  in the last 30 days."], Lines);
    }

    [Fact]
    public async Task Summary_prints_the_report()
    {
        TwoRuns();

        Assert.Equal(0, await Run("summary", "--days", "2"));

        Assert.Equal(ChromeTraces.RenderSummary(ChromeTraces.Summarise(Load()), 2), Lines);
    }

    [Fact]
    public async Task Summary_as_json_keeps_python_key_order()
    {
        TwoRuns();

        Assert.Equal(0, await Run("summary", "--json"));

        var text = _out.ToString();
        var summary = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(
            ["runs", "completed", "ok", "incomplete", "grouping", "waits", "durations", "closed", "blocks", "success_rate"],
            summary.Select(pair => pair.Key));
        Assert.Equal("""{"grouped":1,"deferred":1}""", summary["grouping"]!.ToJsonString());
        Assert.Equal("[]", summary["blocks"]!.ToJsonString());
        Assert.Equal(1.0, summary["success_rate"]!.GetValue<double>());
        Assert.Contains("\"success_rate\": 1.0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_of_nothing_has_a_null_success_rate()
    {
        Assert.Equal(0, await Run("summary", "--json"));

        Assert.Contains("\"success_rate\": null", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prune_lists_and_deletes_the_old_files()
    {
        Directory.CreateDirectory(Traces);
        foreach (var day in new[] { "2026-01-01", "2026-03-04" })
        {
            await File.WriteAllTextAsync(Path.Join(Traces, day + ".ndjson"), string.Empty, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, await Run("prune", "--dry-run"));
        Assert.Equal(["  2026-01-01.ndjson", "Would delete 1 trace files older than 30 days"], Lines);
        Assert.True(File.Exists(Path.Join(Traces, "2026-01-01.ndjson")));

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run("prune", "--keep-days", "90"));
        Assert.Equal(["Deleted 0 trace files older than 90 days"], Lines);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run("prune"));
        Assert.Equal(["  2026-01-01.ndjson", "Deleted 1 trace files older than 30 days"], Lines);
        Assert.False(File.Exists(Path.Join(Traces, "2026-01-01.ndjson")));
    }

    [Theory]
    [InlineData("runs", "--days", "0")]
    [InlineData("runs", "--limit", "0")]
    [InlineData("show", "--days", "-1")]
    [InlineData("summary", "--days", "0")]
    [InlineData("prune", "--keep-days", "0")]
    public void Counts_below_one_do_not_parse(string command, string option, string value)
    {
        var parsed = new ChromeTabsCommand(_ => null, "/home", new FixedTime(Now), TimeZoneInfo.Utc, _out).Build().Parse(["traces", command, option, value]);

        var error = Assert.Single(parsed.Errors);
        Assert.Equal($"Invalid value for '{option}': {value} is not in the range x>=1.", error.Message);
    }

    [Theory]
    [InlineData(false, "chrome-tabs", "traces", "runs")]
    [InlineData(true, "chrome-tabs", "organise")]
    [InlineData(true, "chrome-tabs", "--help")]
    [InlineData(true, "chrome-tabs")]
    public void Only_traces_runs_here_and_the_rest_goes_to_python(bool forwarded, params string[] args)
    {
        var root = new RootCommand { new ChromeTabsCommand(_ => null, "/home", new FixedTime(Now), TimeZoneInfo.Utc, _out).Build() };

        Assert.Equal(forwarded, Rig.Cli.Forwarding.Forwarded.Owns(args, root));
    }

    [Fact]
    public void The_forwarded_row_shows_the_groups_help() =>
        Assert.Contains((ChromeTabsCommand.Name, ChromeTabsCommand.Description), Rig.Cli.Forwarding.Forwarded.Commands.Select(row => (row.Name, row.Description)));

    // Two windows: a "Work" profile window with a PR tab and a plain page, and a minimised "Default" window on no known desktop.
    private static readonly IReadOnlyList<ChromeWindow> Windows =
    [
        new(
            101,
            "Fix it · Pull Request #12 · Owner/Repo",
            "Profile 4",
            "Work",
            1,
            false,
            2,
            [
                new(new RuntimeId([42, 1]), 101, 0, "Fix it · Pull Request #12 · Owner/Repo - 120 MB", "Fix it · Pull Request #12 · Owner/Repo", true, false, true, false)
                {
                    MemoryMb = 120.0,
                    Group = new TabGroup(new RuntimeId([42, 9]), "café"),
                },
                new(new RuntimeId([42, 2]), 101, 1, "Notes", "Notes", false, true, false, true) { Alerts = ["Playing audio"] },
            ])
        {
            Rect = new WindowRect(0, 0, 800, 600),
            Monitor = new ScreenPoint(0, 0),
        },
        new(202, "Home", "Default", null, 2, true, null, [new(new RuntimeId([7]), 202, 0, "Home", "Home", true, false, true, false) { MemoryMb = 80.5 }]),
    ];

    private readonly FakeGitHub _github = new();
    private readonly FakeChromeTabActions _actions = new();
    private readonly FakePresence _presence = new();
    private readonly FakeChromeGroupActions _grouping = new();
    private readonly FakeChromeSavedGroupActions _saved = new();
    private readonly FakeChromeDesktops _desktops = new() { Number = null };
    private long _foreground;
    private IReadOnlyList<ChromeWindow> _windows = Windows;

    private ChromeSession Session(IReadOnlyList<ChromeWindow> windows) =>
        new(() => windows, _actions, _grouping, _saved, _desktops, () => _foreground, _presence, (_, _) => new Dictionary<string, string>());

    private ChromeTabsDriver Driver() => new(() => Session(_windows), _github, () => UrlOwners.None);

    private ChromeTabsCommand WithDriver(bool driver = true) =>
        new(_ => null, "/home", new FixedTime(Now), TimeZoneInfo.Utc, _out, driver ? Driver() : null);

    private async Task<int> Tabs(params string[] args) =>
        await WithDriver().Build().Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

    private const string Issue = "Flaky build · Issue #12 · acme-corp/playbook";

    // Two windows of one profile: the newer (302) shows the issue selected, the older (301) a selected "Home" and the issue twice more.
    private static readonly IReadOnlyList<ChromeWindow> Duplicates =
    [
        new(
            301,
            "Home",
            "Default",
            null,
            1,
            false,
            1,
            [
                new(new RuntimeId([1]), 301, 0, "Home", "Home", true, false, true, false),
                new(new RuntimeId([2]), 301, 1, Issue, Issue, false, false, true, false) { MemoryMb = 50 },
                new(new RuntimeId([3]), 301, 2, Issue, Issue, false, false, false, false),
            ]),
        new(302, Issue, "Default", null, 2, false, 1, [new(new RuntimeId([4]), 302, 0, Issue, Issue, true, false, true, false)]),
    ];

    [Fact]
    public async Task Prune_dry_run_prints_the_plan_and_closes_nothing()
    {
        _windows = Duplicates;

        Assert.Equal(0, await Tabs("prune", "--no-github", "--dry-run"));

        Assert.Empty(_actions.Closed);
        Assert.Contains(Lines, line => line.Contains("Flaky build", StringComparison.Ordinal));
        Assert.Matches(@"^Dry run: nothing closed\. enumerate \d+\.\d\ds, resolve \d+\.\d\ds \(0 repos\), plan \d+\.\d{3}s, total \d+\.\d\ds$", Lines[^1]);
        Assert.Empty(_github.Calls);
    }

    [Fact]
    public async Task Prune_closes_the_duplicates_window_by_window_and_restores_each_selection()
    {
        _windows = Duplicates;

        Assert.Equal(0, await Tabs("prune", "--no-github"));

        Assert.Equal([1, 2], _actions.Closed.Select(tab => tab.Index));
        Assert.Equal([301L], _actions.Restored.SelectMany(restore => restore.Windows));
        Assert.Matches(@"^Closed 2/2 enumerate .*, \d+\.\d\ds per close, total \d+\.\d\ds$", Lines[^1]);
    }

    [Fact]
    public async Task Prune_reports_each_close_that_failed_and_exits_one()
    {
        _windows = Duplicates;
        _actions.FailWith = "tab not found";

        Assert.Equal(1, await Tabs("prune", "--no-github"));

        Assert.Contains(Lines, line => line.StartsWith("Not closed ", StringComparison.Ordinal) && line.EndsWith(": tab not found", StringComparison.Ordinal));
        Assert.StartsWith("Closed 0/2 ", Lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unattended_prune_holds_back_the_foreground_window()
    {
        _windows = Duplicates;
        _foreground = 301;

        Assert.Equal(0, await Tabs("prune", "--no-github", "--unattended"));

        Assert.Empty(_actions.Closed);
        Assert.Contains("Held back: active tab or foreground window (2)", Lines);
        Assert.Equal(2, Lines.Count(line => line.Contains("left for an attended run", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Unattended_prune_leaves_everything_alone_during_a_call()
    {
        _windows = Duplicates;
        _presence.Devices = ["microphone"];

        Assert.Equal(0, await Tabs("prune", "--no-github", "--unattended"));

        Assert.Empty(_actions.Closed);
        Assert.Equal(["Skipped: a call is live: Chrome is using the microphone; left everything for the next run"], Lines);
    }

    [Fact]
    public async Task Prune_with_a_memory_budget_adds_the_memory_plan()
    {
        _windows = Duplicates;

        Assert.Equal(0, await Tabs("prune", "--no-github", "--no-sites", "--max-memory", "0.1", "--dry-run"));

        Assert.NotEmpty(Lines[..^1]);
        Assert.Empty(_actions.Closed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.05")]
    public void Prune_rejects_a_memory_budget_under_a_tenth_of_a_gigabyte(string budget) =>
        Assert.NotEmpty(WithDriver().Build().Parse(["prune", "--max-memory", budget]).Errors);

    [Fact]
    public async Task List_prints_each_window_and_its_tabs_with_flags_memory_and_key()
    {
        Assert.Equal(0, await Tabs("list"));

        Assert.Equal(
            [
                "Window 1 [Work / Profile 4] desktop 2: 2 tabs",
                $"    1 S.L. {TabRank.FormatSize(120),7} owner/repo#12 Fix it · Pull Request #12 · Owner/Repo",
                "    2 .P.A       - title:Notes Notes",
                "Window 2 [Default / Default] desktop ?, minimised: 1 tabs",
                $"    1 S.L. {TabRank.FormatSize(80.5),7} title:Home Home",
            ],
            Lines[..^1]);
        Assert.Matches(@"^2 windows, 3 tabs, .+ reported by tabs, enumerated in \d+\.\d\ds$", Lines[^1]);
    }

    [Fact]
    public async Task List_prs_only_skips_other_tabs_and_profile_picks_windows_by_name_or_directory()
    {
        Assert.Equal(0, await Tabs("list", "--prs-only", "--profile", "WORK"));
        Assert.Equal(3, Lines.Length);
        Assert.Equal("Window 1 [Work / Profile 4] desktop 2: 2 tabs", Lines[0]);
        Assert.Contains("owner/repo#12", Lines[1], StringComparison.Ordinal);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Tabs("list", "--profile", "default"));
        Assert.StartsWith("Window 1 [Default / Default]", Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_as_json_has_the_python_fields_in_order()
    {
        Assert.Equal(0, await Tabs("list", "--json", "--prs-only"));

        var text = _out.ToString();
        Assert.Contains("café", text, StringComparison.Ordinal);
        Assert.Contains("\"memory_mb\": 120.0", text, StringComparison.Ordinal);
        var windows = JsonNode.Parse(text)!.AsArray();
        var first = windows[0]!.AsObject();
        Assert.Equal(
            ["hwnd", "title", "profile_dir", "profile_name", "age_rank", "minimized", "desktop_number", "tabs", "rect", "monitor", "profile_scope"],
            first.Select(pair => pair.Key));
        Assert.Equal("[0,0,800,600]", first["rect"]!.ToJsonString());
        Assert.Equal("[0,0]", first["monitor"]!.ToJsonString());
        var tab = Assert.Single(first["tabs"]!.AsArray())!.AsObject();
        Assert.Equal(
            ["runtime_id", "window_hwnd", "index", "name", "title", "selected", "pinned", "loaded", "audible", "alerts", "memory_mb", "group", "url"],
            tab.Select(pair => pair.Key));
        Assert.Equal(
            """{"runtime_id":[42,9],"title":"café"}""",
            tab["group"]!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Empty(windows[1]!["tabs"]!.AsArray());
        Assert.Null(windows[1]!["rect"]);
    }

    [Fact]
    public async Task Rank_without_github_asks_nothing_and_prints_the_ranking()
    {
        Assert.Equal(0, await Tabs("rank", "--no-github"));

        Assert.Empty(_github.Calls);
        var ranked = TabRank.Rank(Windows, Resolution.Empty);
        Assert.Equal(
            [
                "  #  score   memory  page",
                .. TabPlanText.Rank(ranked),
                $"3 tabs, {ranked.Count(item => item.Protected.Length > 0)} protected, {TabRank.FormatSize(200.5)} reported by tabs",
            ],
            Lines);
    }

    [Fact]
    public async Task Rank_asks_github_about_the_prs_and_limit_keeps_the_lowest()
    {
        Assert.Equal(0, await Tabs("rank", "--limit", "1", "--json"));

        _ = Assert.Single(_github.Calls);
        var row = Assert.Single(JsonNode.Parse(_out.ToString())!.AsArray())!.AsObject();
        Assert.Equal(
            ["label", "title", "profile", "window", "tab", "protected", "score", "memory_mb", "reasons", "flags", "details"],
            row.Select(pair => pair.Key));
        Assert.Equal(TabRank.Rank(Windows, Resolution.Empty)[^1].Label, row["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rank_limit_must_be_positive_and_may_exceed_the_tabs()
    {
        var parsed = WithDriver().Build().Parse(["rank", "--limit", "0"]);
        Assert.Equal("Invalid value for '--limit': 0 is not in the range x>=1.", Assert.Single(parsed.Errors).Message);

        Assert.Equal(0, await Tabs("rank", "--no-github", "--limit", "9"));
        Assert.Equal(5, Lines.Length);
    }

    [Fact]
    public async Task Group_dry_run_prints_the_plan_and_moves_nothing()
    {
        _windows = Duplicates;

        Assert.Equal(0, await Tabs("group", "--dry-run"));

        Assert.Empty(_grouping.Log);
        Assert.Contains(Lines, line => line.Contains("acme-corp/playbook", StringComparison.Ordinal));
        Assert.Matches(@"^Dry run: nothing moved\. planned in \d+\.\d\ds$", Lines[^1]);
    }

    [Fact]
    public async Task Group_runs_the_plan_and_counts_the_moves()
    {
        _windows = Duplicates;

        Assert.Equal(0, await Tabs("group"));

        Assert.NotEmpty(_grouping.Grouped);
        Assert.Matches(@"^Moved (\d+)/\1 in \d+\.\d\ds$", Lines[^1]);
    }

    [Fact]
    public async Task Group_that_stops_exits_1_and_says_how_far_it_got()
    {
        _windows = Duplicates;
        _grouping.GroupFailure = "menu never opened";

        Assert.Equal(1, await Tabs("group"));

        Assert.Matches(@"^Stopped .*menu never opened.*\. 0/\d+ moved\.$", Lines[^1]);
    }

    [Fact]
    public async Task Group_with_nothing_to_move_only_plans()
    {
        Assert.Equal(0, await Tabs("group", "--min-size", "9"));

        Assert.Empty(_grouping.Log);
        Assert.Matches(@"^planned in \d+\.\d\ds$", Lines[^1]);
    }

    [Fact]
    public void Group_min_size_must_be_positive()
    {
        var parsed = WithDriver().Build().Parse(["group", "--min-size", "0"]);

        Assert.Equal("Invalid value for '--min-size': 0 is not in the range x>=1.", Assert.Single(parsed.Errors).Message);
    }

    [Fact]
    public async Task Saved_groups_only_looks_without_consolidate()
    {
        _ = _saved.Bar(101, "Profile 4", "+Work", "-Work");

        Assert.Equal(0, await Tabs("saved-groups"));

        Assert.Empty(_saved.Log);
        Assert.Matches(@"^Looked only: pass --consolidate to delete\. read in \d+\.\d\ds$", Lines[^1]);
    }

    [Fact]
    public async Task Saved_groups_consolidate_deletes_the_closed_copies()
    {
        _ = _saved.Bar(101, "Profile 4", "+Work", "-Work");

        Assert.Equal(0, await Tabs("saved-groups", "--consolidate"));

        Assert.Equal(["101 Profile 4 Work"], _saved.Log);
        Assert.Matches(@"^Deleted 1/1 in \d+\.\d\ds$", Lines[^1]);
    }

    [Fact]
    public async Task Saved_groups_that_fail_exit_1_and_name_the_title()
    {
        _ = _saved.Bar(101, "Profile 4", "+Work", "-Work");
        _saved.Refuses["Work"] = "chip not found";

        Assert.Equal(1, await Tabs("saved-groups", "--consolidate"));

        Assert.Contains("  kept Work: chip not found", Lines);
        Assert.Equal("Stopped deleting saved groups 'Work' [Profile 4]: chip not found. 0/1 deleted.", Lines[^1]);
    }

    [Fact]
    public async Task Saved_groups_with_nothing_to_delete_says_how_long_the_read_took()
    {
        _ = _saved.Bar(101, "Profile 4", "+Work");

        Assert.Equal(0, await Tabs("saved-groups", "--consolidate"));

        Assert.Matches(@"^read in \d+\.\d\ds$", Lines[^1]);
    }

    [Theory]
    [InlineData(true, false, "chrome-tabs", "list")]
    [InlineData(true, false, "chrome-tabs", "rank", "--json")]
    [InlineData(false, true, "chrome-tabs", "list")]
    [InlineData(false, true, "chrome-tabs", "rank")]
    [InlineData(true, false, "chrome-tabs", "prune")]
    [InlineData(false, true, "chrome-tabs", "prune")]
    [InlineData(true, false, "chrome-tabs", "group", "--dry-run")]
    [InlineData(false, true, "chrome-tabs", "group")]
    [InlineData(true, false, "chrome-tabs", "saved-groups")]
    [InlineData(false, true, "chrome-tabs", "saved-groups")]
    [InlineData(true, true, "chrome-tabs", "organise")]
    [InlineData(true, false, "chrome-tabs", "install")]
    [InlineData(true, false, "chrome-tabs", "uninstall")]
    [InlineData(false, true, "chrome-tabs", "install")]
    [InlineData(false, true, "chrome-tabs", "uninstall")]
    public void Driver_commands_run_here_only_with_a_driver(bool withDriver, bool forwarded, params string[] args)
    {
        var root = new RootCommand { WithDriver(withDriver).Build() };

        Assert.Equal(forwarded, Rig.Cli.Forwarding.Forwarded.Owns(args, root));
    }

    private string Home => Path.Join(_temp.Path, "home");

    private string Local => Path.Join(_temp.Path, "local");

    private string Shim => Path.Join(Home, ".local", "bin", "chrome-tabs.cmd");

    private string Target => Path.Join(Local, "shared-utilities", "runtime", "current", ".venv", "Scripts", "desktop-manager.exe");

    private async Task<int> Installs(params string[] args) =>
        await new ChromeTabsCommand(
                name => name == "LOCALAPPDATA" ? Local : null, Home, new FixedTime(Now), TimeZoneInfo.Utc, _out, Driver())
            .Build()
            .Parse(args)
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Install_writes_a_crlf_shim_to_the_runtime_copy_and_says_when_there_is_no_copy_yet()
    {
        Assert.Equal(0, await Installs("install"));

        Assert.Equal($"@echo off\r\n\"{Target}\" chrome-tabs %*\r\n", await File.ReadAllTextAsync(Shim, TestContext.Current.CancellationToken));
        Assert.Equal([$"Command: {Shim} -> {Target}", "No runtime copy yet: run `desktop-manager runtime install`."], Lines);
    }

    [Fact]
    public async Task Install_over_an_existing_copy_rewrites_the_shim_and_says_nothing_more()
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        await File.WriteAllTextAsync(Target, "", TestContext.Current.CancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Shim)!);
        await File.WriteAllTextAsync(Shim, "old", TestContext.Current.CancellationToken);

        Assert.Equal(0, await Installs("install"));

        Assert.StartsWith("@echo off", await File.ReadAllTextAsync(Shim, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal([$"Command: {Shim} -> {Target}"], Lines);
    }

    [Fact]
    public async Task Uninstall_removes_the_shim_and_the_old_snapshot_but_keeps_the_logs()
    {
        var root = Path.Join(Local, "shared-utilities", "chrome-tabs");
        _ = Directory.CreateDirectory(Path.Join(root, "venv", "Scripts"));
        await File.WriteAllTextAsync(Path.Join(root, "organise.log"), "kept", TestContext.Current.CancellationToken);
        Assert.Equal(0, await Installs("install"));
        _out.GetStringBuilder().Clear();

        Assert.Equal(0, await Installs("uninstall"));

        Assert.False(File.Exists(Shim));
        Assert.False(Directory.Exists(Path.Join(root, "venv")));
        Assert.True(File.Exists(Path.Join(root, "organise.log")));
        Assert.Equal([$"Removed the command; kept logs in {root}"], Lines);
    }

    [Fact]
    public async Task Uninstall_with_nothing_installed_still_succeeds()
    {
        Assert.Equal(0, await Installs("uninstall"));

        Assert.Equal([$"Removed the command; kept logs in {Path.Join(Local, "shared-utilities", "chrome-tabs")}"], Lines);
    }

    private IReadOnlyList<TraceRun> Load() => ChromeTraces.Runs(ChromeTraces.LoadSpans(Traces, DateOnly.FromDateTime(Now.UtcDateTime), 30));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
