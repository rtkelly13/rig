using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Cli.Chrome;
using Rig.Core.Chrome;
using Rig.Core.Tests;

namespace Rig.Cli.Tests.Chrome;

/// <summary>
/// <c>rig chrome-tabs traces</c>: the commands over <see cref="ChromeTraces"/>, which its own tests hold to Python's
/// reports. These check what the command adds: the day files it picks, the messages, the JSON and the exit codes.
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

    private IReadOnlyList<TraceRun> Load() => ChromeTraces.Runs(ChromeTraces.LoadSpans(Traces, DateOnly.FromDateTime(Now.UtcDateTime), 30));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
