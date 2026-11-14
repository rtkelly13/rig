using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Traces;
using Rig.Core.Observability;
using Rig.Core.Tests;
using Rig.Core.Tests.Observability;

namespace Rig.Cli.Tests.Traces;

/// <summary>
/// <c>rig traces runs</c> and <c>rig traces summary</c> over fixture day files: the newest-first run table and its
/// limit, tool and window filters, the exact ids and process facts <c>--errors-only</c> prints, the summary text and
/// the documented JSON, the environment override, and the usage errors.
/// </summary>
public sealed class TracesCommandTests : IDisposable
{
    private const string FirstTrace = "aaaaaaaaaaaaaaaa";
    private const string SecondTrace = "bbbbbbbbbbbbbbbb";

    private static readonly DateTimeOffset Now = new(2026, 3, 4, 10, 30, 0, TimeSpan.Zero);
    private static readonly string[] Keys =
    [
        "root", "since", "window", "runs", "spans", "failures", "failure_rate",
        "durations_ms", "run_durations_ms", "tools", "commands", "errors",
    ];

    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _temp.Dispose();
    }

    private string Root => Path.Join(_temp.Path, "checkout", "temp", "traces", "sdk");

    private string Override => Path.Join(_temp.Path, "override");

    private Task<int> Run(params string[] args) => Run(_ => null, args);

    private async Task<int> Run(Func<string, string?> environment, params string[] args)
    {
        _out.GetStringBuilder().Clear();
        _err.GetStringBuilder().Clear();
        return await new TracesCommand(Root, environment, new FixedTime(Now), TimeZoneInfo.Utc, _out, _err)
            .Build()
            .Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);
    }

    private string[] Lines => _out.ToString().Split('\n')[..^1];

    // One estate run that passed and one desktop-manager run whose root timed out and whose child exited with a 401.
    private void TwoRuns(string? root = null)
    {
        var folder = root ?? Root;
        TraceFixtures.Write(
            folder,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("estate sync", "s1", traceId: FirstTrace, start: 0, end: 2),
            TraceFixtures.Span(
                "git status",
                "s2",
                traceId: FirstTrace,
                parent: "s1",
                start: 0.1,
                end: 0.2,
                attributes: [("process.command", "git status")]));
        TraceFixtures.Write(
            folder,
            "desktop-manager",
            "2026-03-04",
            TraceFixtures.Span(
                "rig gh pr wait",
                "r1",
                traceId: SecondTrace,
                start: 10,
                end: 13,
                status: 2,
                message: "timed out",
                attributes: [("process.command", "rig gh pr wait"), ("process.cwd", "/code/rig")]),
            TraceFixtures.Span(
                "gh pr checks",
                "r2",
                traceId: SecondTrace,
                parent: "r1",
                start: 10.5,
                end: 12,
                attributes: [("process.command", "gh pr checks"), ("process.exit_code", 1), ("process.stderr_snippet", "HTTP 401")]));
    }

    // -- runs -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Runs_lists_the_newest_run_first()
    {
        TwoRuns();

        Assert.Equal(0, await Run("runs"));

        var lines = Lines;
        Assert.StartsWith("trace     tool             command", lines[0], StringComparison.Ordinal);
        Assert.Contains("started", lines[0], StringComparison.Ordinal);
        Assert.Contains(SecondTrace[..8], lines[1], StringComparison.Ordinal);
        Assert.Contains("desktop-manager", lines[1], StringComparison.Ordinal);
        Assert.Contains("rig gh pr wait", lines[1], StringComparison.Ordinal);
        Assert.Contains("2026-03-04 10:00:10", lines[1], StringComparison.Ordinal);
        Assert.Contains("3.00s", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("2", lines[1], StringComparison.Ordinal);
        Assert.Contains(FirstTrace[..8], lines[2], StringComparison.Ordinal);
        Assert.Contains("estate sync", lines[2], StringComparison.Ordinal);
        Assert.Contains("2.00s", lines[2], StringComparison.Ordinal);
        Assert.Equal(3, lines.Length);
        Assert.Empty(_err.ToString());
    }

    [Fact]
    public async Task Runs_limit_keeps_the_most_recent_rows()
    {
        TwoRuns();

        Assert.Equal(0, await Run("runs", "--limit", "1"));

        var line = Assert.Single(Lines[1..]);
        Assert.Contains(SecondTrace[..8], line, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstTrace[..8], _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runs_tool_keeps_only_that_tool()
    {
        TwoRuns();

        Assert.Equal(0, await Run("runs", "--tool", "estate"));

        var line = Assert.Single(Lines[1..]);
        Assert.Contains("estate", line, StringComparison.Ordinal);
        Assert.DoesNotContain("desktop-manager", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runs_window_keeps_the_runs_inside_it()
    {
        TwoRuns();

        Assert.Equal(0, await Run("runs", "--since", "30m"));
        Assert.Equal(2, Lines.Length - 1);

        Assert.Equal(0, await Run("runs", "--since", "10m"));
        Assert.Equal($"No trace runs under {Root}.\n", _out.ToString());
    }

    [Fact]
    public async Task Runs_errors_only_prints_each_failures_ids_and_process_facts()
    {
        TwoRuns();

        Assert.Equal(0, await Run("runs", "--errors-only"));

        var text = _out.ToString();
        Assert.Contains(SecondTrace[..8], text, StringComparison.Ordinal);
        Assert.DoesNotContain(FirstTrace[..8], text, StringComparison.Ordinal);
        Assert.Contains("Failed operations (2):\n", text, StringComparison.Ordinal);
        Assert.Contains($"  traceId={SecondTrace} spanId=r1 tool=desktop-manager span=rig gh pr wait\n", text, StringComparison.Ordinal);
        Assert.Contains($"  traceId={SecondTrace} spanId=r2 tool=desktop-manager span=gh pr checks\n", text, StringComparison.Ordinal);
        Assert.Contains("    process.command: rig gh pr wait\n", text, StringComparison.Ordinal);
        Assert.Contains("    process.cwd: /code/rig\n", text, StringComparison.Ordinal);
        Assert.Contains("    process.exit_code: -\n", text, StringComparison.Ordinal);
        Assert.Contains("    process.exit_code: 1\n", text, StringComparison.Ordinal);
        Assert.Contains("    process.stderr_snippet: HTTP 401\n", text, StringComparison.Ordinal);
        Assert.Contains("    error: timed out\n", text, StringComparison.Ordinal);
        Assert.Contains("    error: HTTP 401\n", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("spanId=r1", StringComparison.Ordinal) < text.IndexOf("spanId=r2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runs_errors_only_without_failures_says_so()
    {
        TraceFixtures.Write(Root, "estate", "2026-03-04", TraceFixtures.Span("estate sync", "s1", traceId: FirstTrace));

        Assert.Equal(0, await Run("runs", "--errors-only"));

        Assert.Equal($"No failed trace runs under {Root}.\n", _out.ToString());
    }

    [Fact]
    public async Task An_empty_folder_says_where_it_looked()
    {
        Assert.Equal(0, await Run("runs"));

        Assert.Equal($"No trace runs under {Root}.\n", _out.ToString());
    }

    [Fact]
    public async Task A_failure_with_nothing_recorded_still_lists_dashes()
    {
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("git status", "s1", traceId: FirstTrace, status: 2, attributes: [("process.exit_code", 1)]));

        Assert.Equal(0, await Run("runs", "--errors-only"));

        Assert.Contains("    process.command: -\n", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("    process.cwd: -\n", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("    process.stderr_snippet: \n", _out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("    error:", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_short_run_is_shown_in_milliseconds()
    {
        TraceFixtures.Write(Root, "estate", "2026-03-04", TraceFixtures.Span("quick", "s1", start: 0, end: 0.0005));

        Assert.Equal(0, await Run("runs"));

        Assert.Contains("0.500ms", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_command_is_clipped_to_one_line()
    {
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("run", "s1", attributes: [("process.command", "git log --oneline --first-parent --decorate --all --graph --abbrev-commit\nand more")]));

        Assert.Equal(0, await Run("runs"));

        var text = _out.ToString();
        Assert.Contains("...", text, StringComparison.Ordinal);
        Assert.DoesNotContain("and more", text, StringComparison.Ordinal);
        Assert.DoesNotContain("--abbrev-commit\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runs_rejects_a_limit_below_one()
    {
        Assert.Equal(2, await Run("runs", "--limit", "0"));

        Assert.Equal("Invalid value for --limit: 0 is not in the range x>=1.\n", _err.ToString());
        Assert.Empty(_out.ToString());
    }

    [Fact]
    public async Task Runs_rejects_an_invalid_window()
    {
        Assert.Equal(2, await Run("runs", "--since", "soon"));

        Assert.Equal("rig: Invalid window 'soon'. Use a value like 24h or 7d.\n", _err.ToString());
        Assert.Empty(_out.ToString());
    }

    // -- summary --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Summary_prints_the_totals_percentiles_and_breakdowns()
    {
        TwoRuns();

        Assert.Equal(0, await Run("summary"));

        var text = _out.ToString();
        Assert.Contains("Trace summary: last 24h (since 2026-03-03 10:30:00 UTC)\n", text, StringComparison.Ordinal);
        Assert.Contains("  runs          2\n", text, StringComparison.Ordinal);
        Assert.Contains("  spans         4\n", text, StringComparison.Ordinal);
        Assert.Contains("  failed spans  2 (50.0%)\n", text, StringComparison.Ordinal);
        Assert.Contains("  span duration p50 1.50s, p90 3.00s, p99 3.00s\n", text, StringComparison.Ordinal);
        Assert.Contains("  run duration  p50 2.00s, p90 3.00s, p99 3.00s\n", text, StringComparison.Ordinal);
        Assert.Contains("By tool\n", text, StringComparison.Ordinal);
        Assert.Contains("By command\n", text, StringComparison.Ordinal);
        Assert.Contains("Recent errors\n", text, StringComparison.Ordinal);
        Assert.Contains("timed out", text, StringComparison.Ordinal);
        Assert.Contains("HTTP 401", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_says_when_the_window_holds_no_traces()
    {
        Assert.Equal(0, await Run("summary"));

        Assert.Contains($"  spans         0\n", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains($"No traces under {Root} in this window.\n", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_json_is_the_documented_schema()
    {
        TwoRuns();

        Assert.Equal(0, await Run("summary", "--json"));

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(_out.ToString()));
        Assert.Equal(Keys, json.Select(pair => pair.Key));
        Assert.Equal(Root, json["root"]!.GetValue<string>());
        Assert.Equal("2026-03-03T10:30:00Z", json["since"]!.GetValue<string>());
        Assert.Equal("24h", json["window"]!.GetValue<string>());
        Assert.Equal(2, json["runs"]!.GetValue<int>());
        Assert.Equal(4, json["spans"]!.GetValue<int>());
        Assert.Equal(2, json["failures"]!.GetValue<int>());
        Assert.Equal(0.5, json["failure_rate"]!.GetValue<double>());
        Assert.Equal(1500, json["durations_ms"]!["p50"]!.GetValue<double>());
        Assert.Equal(3000, json["durations_ms"]!["p90"]!.GetValue<double>());
        Assert.Equal(3000, json["durations_ms"]!["p99"]!.GetValue<double>());
        Assert.Equal(2000, json["run_durations_ms"]!["p50"]!.GetValue<double>());
        Assert.Equal(["name", "runs", "spans", "failures"], json["tools"]![0]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(["name", "runs", "spans", "failures"], json["commands"]![0]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(
            ["trace_id", "span_id", "tool", "span", "command", "cwd", "exit_code", "status_code", "stderr", "exception", "exception_message", "message"],
            json["errors"]![0]!.AsObject().Select(pair => pair.Key));
        Assert.Equal("timed out", json["errors"]![0]!["message"]!.GetValue<string>());
        Assert.Null(json["errors"]![0]!["exit_code"]);
        Assert.Equal(1, json["errors"]![1]!["exit_code"]!.GetValue<long>());
        Assert.Equal("HTTP 401", json["errors"]![1]!["stderr"]!.GetValue<string>());
    }

    [Fact]
    public async Task Summary_json_of_an_empty_window_has_empty_collections()
    {
        Assert.Equal(0, await Run("summary", "--json"));

        var json = Assert.IsType<JsonObject>(JsonNode.Parse(_out.ToString()));
        Assert.Equal(0, json["runs"]!.GetValue<int>());
        Assert.Equal(0, json["failure_rate"]!.GetValue<double>());
        Assert.Empty(json["tools"]!.AsArray());
        Assert.Empty(json["commands"]!.AsArray());
        Assert.Empty(json["errors"]!.AsArray());
        Assert.Equal(0, json["durations_ms"]!["p50"]!.GetValue<double>());
    }

    [Fact]
    public async Task Summary_rejects_an_invalid_window()
    {
        Assert.Equal(2, await Run("summary", "--since", "yesterday"));

        Assert.Equal("rig: Invalid window 'yesterday'. Use a value like 24h or 7d.\n", _err.ToString());
    }

    // -- the folder -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_directory_variable_replaces_the_default_root()
    {
        TwoRuns(Override);

        var exit = await Run(key => key == TraceAnalytics.DirVariable ? Override : null, "runs");

        Assert.Equal(0, exit);
        Assert.Contains(SecondTrace[..8], _out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain($"No trace runs under {Root}", _out.ToString(), StringComparison.Ordinal);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
