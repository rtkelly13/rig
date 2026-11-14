using System.Text.Json.Nodes;
using Rig.Core.Observability;

namespace Rig.Core.Tests.Observability;

/// <summary>
/// <c>TraceAnalytics</c> over fixture day files: reading and grouping spans into runs, the failure rules, duration
/// percentiles, breakdowns and recent errors, the window that picks files, and the tolerant handling of a corrupt
/// line, an empty file or a missing folder.
/// </summary>
public sealed class TraceAnalyticsTests : IDisposable
{
    private const long Second = 1_000_000_000;

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Root => Path.Join(_temp.Path, "traces");

    private static DateTimeOffset At(double seconds) =>
        new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    // -- reading and grouping -------------------------------------------------------------------------------------

    [Fact]
    public void A_missing_folder_reads_as_an_empty_dataset()
    {
        var dataset = TraceAnalytics.Load(Path.Join(_temp.Path, "gone"));

        Assert.Empty(dataset.Runs);
        Assert.Empty(dataset.Spans);
        Assert.Empty(dataset.Files);
    }

    [Fact]
    public void A_root_that_is_a_file_reads_as_an_empty_dataset()
    {
        var file = _temp.File("traces");

        var dataset = TraceAnalytics.Load(file);

        Assert.Empty(dataset.Spans);
    }

    [Fact]
    public void Spans_are_grouped_into_runs_by_trace_and_the_root_names_the_run()
    {
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("estate sync", "root", start: 0, end: 2),
            TraceFixtures.Span(
                "git fetch",
                "child",
                parent: "root",
                start: 0.2,
                end: 1.4,
                attributes: [("process.command", "git fetch"), ("process.cwd", "/code/rig")]));
        TraceFixtures.Write(
            Root,
            "desktop-manager",
            "2026-03-04",
            TraceFixtures.Span("workspace restore", "restore", traceId: "trace-0002", start: 10, end: 12));

        var dataset = TraceAnalytics.Load(Root);

        Assert.Equal(3, dataset.Spans.Count);
        Assert.Equal(2, dataset.Runs.Count);
        var run = dataset.Runs[0];
        Assert.Equal("trace-0001", run.TraceId);
        Assert.Equal("estate", run.Tool);
        Assert.Equal("estate sync", run.Name);
        Assert.Equal("estate sync", run.Command);
        Assert.Equal(2000, run.DurationMs);
        Assert.False(run.Failed);
        Assert.Equal(0, run.Failures);
        Assert.Equal(2, run.Spans.Count);
        Assert.Equal("trace-0002", dataset.Runs[1].TraceId);
        Assert.Equal("desktop-manager", dataset.Runs[1].Tool);
        Assert.Equal([.. dataset.Files], [.. Directory.EnumerateFiles(Root, "*.ndjson", SearchOption.AllDirectories).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void The_root_is_the_earliest_span_without_a_parent()
    {
        // The parentless span starts after a child: the run still takes its tool and name from the parentless span.
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("root", "root-span", start: 5, end: 6),
            TraceFixtures.Span("child", "child-span", parent: "root-span", start: 0, end: 7));

        var run = Assert.Single(TraceAnalytics.Load(Root).Runs);

        Assert.Equal("root", run.Name);
        Assert.Equal(0, run.StartNs - TraceFixtures.Base);
        Assert.Equal(7 * Second, run.EndNs - TraceFixtures.Base);
    }

    [Fact]
    public void A_run_without_a_parentless_span_uses_its_earliest_span()
    {
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("later", "second", parent: "first", start: 1, end: 2),
            TraceFixtures.Span("earlier", "first", parent: "second", start: 0, end: 3));

        var run = Assert.Single(TraceAnalytics.Load(Root).Runs);

        Assert.Equal("earlier", run.Name);
    }

    [Fact]
    public void A_complete_copy_replaces_an_incomplete_one()
    {
        var path = TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("sync", "span", start: 0, end: 1, attributes: [("span.incomplete", true)]));
        File.AppendAllText(path, TraceFixtures.Envelope("estate", TraceFixtures.Span("sync", "span", start: 0, end: 2)) + "\n");

        var span = Assert.Single(TraceAnalytics.Load(Root).Spans);

        Assert.False(span.Incomplete);
        Assert.Equal(2000, span.DurationMs);
    }

    [Fact]
    public void A_later_incomplete_copy_does_not_replace_the_complete_one()
    {
        var path = TraceFixtures.Write(Root, "estate", "2026-03-04", TraceFixtures.Span("sync", "span", start: 0, end: 2));
        File.AppendAllText(
            path,
            TraceFixtures.Envelope("estate", TraceFixtures.Span("sync", "span", start: 0, end: 1, attributes: [("span.incomplete", true)])) + "\n");

        var span = Assert.Single(TraceAnalytics.Load(Root).Spans);

        Assert.False(span.Incomplete);
        Assert.Equal(2000, span.DurationMs);
    }

    [Fact]
    public void The_same_span_in_two_files_is_counted_once()
    {
        TraceFixtures.Write(Root, "estate", "2026-03-04", TraceFixtures.Span("sync", "span", start: 0, end: 2));
        var copied = TraceFixtures.Write(Root, "estate", "2026-03-05", TraceFixtures.Span("sync", "span", start: 0, end: 3));

        var dataset = TraceAnalytics.Load(Root);

        Assert.Single(dataset.Spans);
        Assert.Equal(2, dataset.Files.Count);
        Assert.EndsWith("2026-03-05.ndjson", copied, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tool_falls_back_to_the_folder_below_the_root()
    {
        TraceFixtures.Write(Root, "fallback-tool", tool: null!, "2026-03-04", TraceFixtures.Span("sync", "span"));

        var span = Assert.Single(TraceAnalytics.Load(Root).Spans);

        Assert.Equal("fallback-tool", span.Tool);
    }

    [Fact]
    public void A_file_directly_under_the_root_has_an_unknown_tool()
    {
        TraceFixtures.Write(Root, string.Empty, tool: null!, "2026-03-04", TraceFixtures.Span("sync", "span"));

        var span = Assert.Single(TraceAnalytics.Load(Root).Spans);

        Assert.Equal("unknown", span.Tool);
    }

    [Fact]
    public void The_scope_name_is_read()
    {
        TraceFixtures.Write(Root, "estate", "2026-03-04", TraceFixtures.Span("sync", "span"));

        Assert.Equal("rig.proc.git", Assert.Single(TraceAnalytics.Load(Root).Spans).Scope);
    }

    [Fact]
    public void A_span_that_ended_before_it_started_has_zero_duration()
    {
        TraceFixtures.Write(Root, "estate", "2026-03-04", TraceFixtures.Span("backwards", "span", start: 2, end: 1));

        Assert.Equal(0, Assert.Single(TraceAnalytics.Load(Root).Spans).DurationMs);
    }

    [Fact]
    public void A_span_without_end_time_ends_when_it_started()
    {
        var raw = TraceFixtures.Span("sync", "span");
        _ = raw.Remove("endTimeUnixNano");
        TraceFixtures.Write(Root, "estate", "2026-03-04", raw);

        Assert.Equal(0, Assert.Single(TraceAnalytics.Load(Root).Spans).DurationMs);
    }

    // -- failures and errors --------------------------------------------------------------------------------------

    [Fact]
    public void A_span_fails_on_an_error_status_or_a_nonzero_exit_code()
    {
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("status", "s1", status: 2, message: "the command failed"),
            TraceFixtures.Span("exit", "s2", attributes: [("process.exit_code", 7)]),
            TraceFixtures.Span("alternate-exit", "s3", attributes: [("process.exit.code", "3")]),
            TraceFixtures.Span("passed", "s4"),
            TraceFixtures.Span("unset", "s5", status: null));

        var spans = TraceAnalytics.Load(Root).Spans;

        Assert.Equal([true, true, true, false, false], spans.Select(span => span.Failed));
        Assert.Equal(7, spans[1].ExitCode);
        Assert.Equal(3, spans[2].ExitCode);
        Assert.Null(spans[4].StatusCode);
    }

    [Fact]
    public void The_error_prefers_the_status_then_the_exception_then_the_stderr_snippet()
    {
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("status", "s1", status: 2, message: "status message", attributes: [("process.stderr_snippet", "stderr text")]),
            TraceFixtures.Span(
                "exception",
                "s2",
                status: 2,
                events: [("exception", 0.5, [("exception.type", "System.IO.IOException"), ("exception.message", "disk full")])],
                attributes: [("process.stderr_snippet", "stderr text")]),
            TraceFixtures.Span("stderr", "s3", status: 2, attributes: [("process.stderr_snippet", "stderr text")]));

        var spans = TraceAnalytics.Load(Root).Spans;

        Assert.Equal(["status message", "disk full", "stderr text"], spans.Select(span => span.Error));
        Assert.Equal("System.IO.IOException", spans[1].ExceptionType);
        Assert.Equal("disk full", spans[1].ExceptionMessage);
        Assert.Empty(spans[0].ExceptionType);
    }

    [Fact]
    public void The_command_falls_back_to_the_process_args_then_the_span_name()
    {
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("run", "s1", attributes: [("process.command", "git status"), ("process.command_args", "ignored")]),
            TraceFixtures.Span("args", "s2", attributes: [("process.command", string.Empty), ("process.command_args", "uv run pytest")]),
            TraceFixtures.Span("named", "s3"));

        var commands = TraceAnalytics.Load(Root).Spans.Select(TraceAnalytics.Command).ToList();

        Assert.Equal(["git status", "uv run pytest", "named"], commands);
    }

    [Fact]
    public void Historical_python_and_csharp_spans_normalise_to_the_same_facts()
    {
        // rig.tracing (Python) writes process.exit_code; the C# root span (#215) writes process.exit.code. Both are
        // read the same way, so a report can mix the two languages' files.
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span(
                "subprocess: git fetch",
                "python-span",
                start: 0,
                end: 1,
                status: 2,
                message: "fatal: repository not found",
                attributes:
                [
                    ("process.command", "git fetch"),
                    ("process.cwd", "/code/rig"),
                    ("process.exit_code", 128),
                    ("process.stderr_snippet", "fatal: repository not found"),
                ]),
            TraceFixtures.Span(
                "rig status",
                "csharp-span",
                traceId: "trace-0002",
                start: 2,
                end: 3,
                status: 2,
                message: "exit code 2",
                attributes:
                [
                    ("process.cwd", "/code/rig"),
                    ("process.exit.code", 2),
                    ("cli.argv", new object[] { "rig", "status" }),
                ]));

        var spans = TraceAnalytics.Load(Root).Spans;

        Assert.Equal([128L, 2L], spans.Select(span => span.ExitCode!.Value));
        Assert.Equal([true, true], spans.Select(span => span.Failed));
        Assert.Equal(["git fetch", "rig status"], spans.Select(TraceAnalytics.Command));
        Assert.All(spans, span => Assert.Equal("/code/rig", span.Cwd));
        Assert.Equal(["fatal: repository not found", "exit code 2"], spans.Select(span => span.Error));
        Assert.Equal(["rig", "status"], [.. Assert.IsAssignableFrom<List<object?>>(spans[1].Attributes["cli.argv"]).Select(item => item as string)]);
    }

    [Fact]
    public void A_span_with_neither_a_command_nor_a_name_is_unknown()
    {
        TraceFixtures.Write(Root, "estate", "2026-03-04", TraceFixtures.Span(string.Empty, "span"));

        Assert.Equal("unknown", TraceAnalytics.Command(Assert.Single(TraceAnalytics.Load(Root).Spans)));
    }

    [Fact]
    public void Errors_are_the_newest_failed_spans_first_and_limited()
    {
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("old", "s1", traceId: "trace-0001", start: 0, end: 1, status: 2),
            TraceFixtures.Span("middle", "s2", traceId: "trace-0002", start: 2, end: 3, status: 2),
            TraceFixtures.Span("newest", "s3", traceId: "trace-0003", start: 4, end: 5, status: 2),
            TraceFixtures.Span("passed", "s4", traceId: "trace-0004", start: 6, end: 7));

        var errors = TraceAnalytics.Errors(TraceAnalytics.Load(Root).Spans);

        Assert.Equal(["newest", "middle", "old"], errors.Select(error => error.Span));
        Assert.Equal("trace-0003", errors[0].TraceId);
        Assert.Equal("s3", errors[0].SpanId);
        Assert.Equal(5 * Second, errors[0].EndNs - TraceFixtures.Base);
        Assert.Single(TraceAnalytics.Errors(TraceAnalytics.Load(Root).Spans, limit: 1));
        Assert.Empty(TraceAnalytics.Errors(TraceAnalytics.Load(Root).Spans, limit: 0));
    }

    [Fact]
    public void Negative_error_limits_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceAnalytics.Errors([], limit: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceAnalytics.Summarize(new TraceDataset([], [], []), errorLimit: -1));
    }

    // -- percentiles and summary ----------------------------------------------------------------------------------

    [Fact]
    public void Percentiles_are_nearest_rank()
    {
        Assert.Equal(new TracePercentiles(50, 90, 99), TraceAnalytics.Percentiles(Enumerable.Range(1, 100).Select(value => (double)value)));
        Assert.Equal(new TracePercentiles(2, 3, 3), TraceAnalytics.Percentiles([3.0, 1.0, 2.0]));
        Assert.Equal(new TracePercentiles(0, 0, 0), TraceAnalytics.Percentiles([]));
    }

    [Fact]
    public void A_summary_counts_runs_spans_failures_and_durations()
    {
        TwoRuns();
        var dataset = TraceAnalytics.Load(Root);

        var summary = TraceAnalytics.Summarize(dataset);

        Assert.Equal(2, summary.Runs);
        Assert.Equal(5, summary.Spans);
        Assert.Equal(2, summary.Failures);
        Assert.Equal(0.4, summary.FailureRate, 6);
        Assert.Equal(new TracePercentiles(1500, 3000, 3000), summary.SpanDurations);
        Assert.Equal(new TracePercentiles(2000, 3000, 3000), summary.RunDurations);
        Assert.Equal(["git status", "gh pr checks", "rig gh pr wait", "estate sync"], summary.Commands.Select(row => row.Name));
        Assert.Equal([2, 1, 1, 1], summary.Commands.Select(row => row.Spans));
        Assert.Equal([0, 1, 1, 0], summary.Commands.Select(row => row.Failures));
        Assert.Equal(["estate", "desktop-manager"], summary.Tools.Select(row => row.Name));
        Assert.Equal([1, 1], summary.Tools.Select(row => row.Runs));
        Assert.Equal([0, 2], summary.Tools.Select(row => row.Failures));
        Assert.Equal(["rig gh pr wait", "gh pr checks"], summary.Errors.Select(error => error.Span));
        Assert.Equal("timed out", summary.Errors[0].Message);
        Assert.Equal("/code/rig", summary.Errors[0].Cwd);
        Assert.Equal(1, summary.Errors[1].ExitCode);
        Assert.Equal("HTTP 401", summary.Errors[1].Message);
    }

    [Fact]
    public void An_empty_dataset_summarises_as_zero_with_no_rows()
    {
        var summary = TraceAnalytics.Summarize(TraceAnalytics.Load(Path.Join(_temp.Path, "gone")));

        Assert.Equal(0, summary.Runs);
        Assert.Equal(0, summary.Spans);
        Assert.Equal(0, summary.Failures);
        Assert.Equal(0, summary.FailureRate);
        Assert.Equal(new TracePercentiles(0, 0, 0), summary.SpanDurations);
        Assert.Empty(summary.Tools);
        Assert.Empty(summary.Commands);
        Assert.Empty(summary.Errors);
    }

    [Fact]
    public void A_breakdown_without_a_name_is_unknown()
    {
        TraceFixtures.Write(Root, "estate", "2026-03-04", TraceFixtures.Span(string.Empty, "span"));

        var summary = TraceAnalytics.Summarize(TraceAnalytics.Load(Root));

        Assert.Equal("unknown", Assert.Single(summary.Commands).Name);
    }

    // -- the window -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_window_drops_spans_that_ended_before_it()
    {
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("old", "s1", traceId: "trace-0001", start: 0, end: 2),
            TraceFixtures.Span("new", "s2", traceId: "trace-0002", start: 3, end: 8));

        var dataset = TraceAnalytics.Load(Root, At(5));

        var span = Assert.Single(dataset.Spans);
        Assert.Equal("new", span.Name);
        Assert.Single(dataset.Runs);
    }

    [Fact]
    public void A_day_file_older_than_the_window_is_not_read()
    {
        TraceFixtures.Write(Root, "estate", "2026-03-02", TraceFixtures.Span("stale", "span", start: 0, end: 9));
        TraceFixtures.Write(Root, "estate", "2026-03-03", TraceFixtures.Span("edge", "edge-span", start: 0, end: 9));

        var dataset = TraceAnalytics.Load(Root, At(0));

        Assert.Equal([Path.Join(Root, "estate", "2026-03-03.ndjson")], dataset.Files);
        Assert.Single(dataset.Spans);
    }

    [Fact]
    public void A_file_not_named_for_a_day_is_read_when_it_was_modified_inside_the_window()
    {
        var path = TraceFixtures.Write(Root, "estate", "imported", TraceFixtures.Span("copied", "span"));
        File.SetLastWriteTimeUtc(path, At(-3600).UtcDateTime);

        Assert.Empty(TraceAnalytics.Load(Root, At(0)).Files);

        File.SetLastWriteTimeUtc(path, At(60).UtcDateTime);
        Assert.Single(TraceAnalytics.Load(Root, At(0)).Files);
    }

    [Fact]
    public void A_day_file_with_an_impossible_date_is_judged_by_its_modification_time()
    {
        var path = TraceFixtures.Write(Root, "estate", "2026-13-01", TraceFixtures.Span("span", "span"));
        File.SetLastWriteTimeUtc(path, At(-3600).UtcDateTime);

        Assert.Empty(TraceAnalytics.Load(Root, At(0)).Files);

        File.SetLastWriteTimeUtc(path, At(60).UtcDateTime);
        Assert.Single(TraceAnalytics.Load(Root, At(0)).Files);
    }

    [Fact]
    public void A_tool_filter_keeps_only_that_tools_spans()
    {
        TwoRuns();

        var dataset = TraceAnalytics.Load(Root, tool: "DESKTOP-MANAGER");

        Assert.Equal(["desktop-manager"], dataset.Spans.Select(span => span.Tool).Distinct(StringComparer.Ordinal));
        Assert.Single(dataset.Runs);
    }

    // -- corrupt input --------------------------------------------------------------------------------------------

    [Fact]
    public void A_corrupt_line_an_empty_file_and_a_non_envelope_are_skipped()
    {
        var path = TraceFixtures.Write(Root, "estate", "2026-03-04", TraceFixtures.Span("kept", "span"));
        File.AppendAllText(path, "{not json\n\n{}\n[]\n" + TraceFixtures.Envelope("estate", TraceFixtures.Span("also kept", "span-2")) + "\n");
        _temp.File(Path.Join("traces", "estate", "2026-03-05.ndjson"));

        var spans = TraceAnalytics.Load(Root).Spans.Select(span => span.Name).ToList();

        Assert.Equal(["kept", "also kept"], spans);
    }

    [Fact]
    public void Malformed_attributes_and_events_read_as_absent()
    {
        var raw = TraceFixtures.Span("sync", "span");
        raw["attributes"] = new JsonObject { ["not"] = "a list" };
        raw["events"] = new JsonArray("not an object");
        TraceFixtures.Write(Root, "estate", "2026-03-04", raw);

        var span = Assert.Single(TraceAnalytics.Load(Root).Spans);

        Assert.Empty(span.Attributes);
        Assert.Empty(span.Events);
        Assert.Null(span.ExitCode);
    }

    [Fact]
    public void Every_attribute_value_shape_is_read()
    {
        var raw = TraceFixtures.Span("sync", "span");
        raw["attributes"] = new JsonArray(
            new JsonObject { ["key"] = "text", ["value"] = new JsonObject { ["stringValue"] = "value" } },
            new JsonObject { ["key"] = "flag", ["value"] = new JsonObject { ["boolValue"] = true } },
            new JsonObject { ["key"] = "whole", ["value"] = new JsonObject { ["intValue"] = "42" } },
            new JsonObject { ["key"] = "real", ["value"] = new JsonObject { ["doubleValue"] = 1.5 } },
            new JsonObject
            {
                ["key"] = "list",
                ["value"] = new JsonObject
                {
                    ["arrayValue"] = new JsonObject
                    {
                        ["values"] = new JsonArray(new JsonObject { ["stringValue"] = "one" }),
                    },
                },
            },
            new JsonObject { ["key"] = 1, ["value"] = new JsonObject { ["stringValue"] = "ignored" } },
            new JsonObject { ["key"] = "bad", ["value"] = "not an object" },
            new JsonObject { ["key"] = "other", ["value"] = new JsonObject() });
        TraceFixtures.Write(Root, "estate", "2026-03-04", raw);

        var attributes = Assert.Single(TraceAnalytics.Load(Root).Spans).Attributes;

        Assert.Equal("value", attributes["text"]);
        Assert.True(attributes["flag"] is true);
        Assert.Equal(42L, attributes["whole"]);
        Assert.Equal(1.5, attributes["real"]);
        Assert.Equal(new List<object?> { "one" }, Assert.IsAssignableFrom<List<object?>>(attributes["list"]));
        Assert.False(attributes.ContainsKey("1"));
        Assert.Null(attributes["bad"]);
        Assert.Null(attributes["other"]);
    }

    [Fact]
    public void Lax_numeric_fields_accept_numbers_bools_and_strings()
    {
        // OTLP-JSON writers disagree on number encodings: pydantic and both rig writers may send a string, and a
        // foreign collector may send a bool or a double. Each reads as the whole number it means, or as absent.
        var doubleStatus = TraceFixtures.Span("double-status", "s1", status: null);
        doubleStatus["status"] = new JsonObject { ["code"] = 2.0 };
        var trueStatus = TraceFixtures.Span("true-status", "s2", status: null);
        trueStatus["status"] = new JsonObject { ["code"] = true };
        var falseStatus = TraceFixtures.Span("false-status", "s3", status: null);
        falseStatus["status"] = new JsonObject { ["code"] = false };
        var unknownStatus = TraceFixtures.Span("unknown-status", "s4", status: null);
        unknownStatus["status"] = new JsonObject { ["code"] = new JsonObject { ["not"] = "a number" } };
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            doubleStatus,
            trueStatus,
            falseStatus,
            unknownStatus,
            TraceFixtures.Span("bool-exit", "s5", attributes: [("process.exit_code", true)]),
            TraceFixtures.Span("false-exit", "s6", attributes: [("process.exit_code", false)]),
            TraceFixtures.Span("double-exit", "s7", attributes: [("process.exit_code", 2.0)]),
            TraceFixtures.Span("fractional-exit", "s8", attributes: [("process.exit_code", 2.5)]),
            TraceFixtures.Span("spaced-exit", "s9", attributes: [("process.exit_code", " 3 ")]),
            TraceFixtures.Span("text-exit", "s10", attributes: [("process.exit_code", "nope")]));

        var spans = TraceAnalytics.Load(Root).Spans;

        Assert.Equal(new int?[] { 2, 1, 0, null }, [.. spans.Take(4).Select(span => span.StatusCode)]);
        Assert.Equal([true, false, true, false], [.. spans.Take(4).Select(span => span.Failed)]);
        Assert.Equal(new long?[] { 1, 0, 2, null, 3, null }, [.. spans.Skip(4).Select(span => span.ExitCode)]);
        Assert.Equal([true, false, true, false, true, false], [.. spans.Skip(4).Select(span => span.Failed)]);
    }

    // -- the window text and root ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("15m", 900)]
    [InlineData("24h", 86400)]
    [InlineData("7d", 604800)]
    [InlineData("2w", 1209600)]
    [InlineData(" 7D ", 604800)]
    public void A_window_is_parsed_to_its_span(string text, double seconds)
    {
        var window = TraceWindow.Parse(text);

        Assert.Equal(text.Trim(), window.Text);
        Assert.Equal(seconds, window.Span.TotalSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("24")]
    [InlineData("0h")]
    [InlineData("-1h")]
    [InlineData("1.5h")]
    [InlineData("h")]
    [InlineData("24H!")]
    public void An_invalid_window_is_refused(string text) =>
        Assert.Throws<FormatException>(() => TraceWindow.Parse(text));

    [Fact]
    public void The_root_prefers_the_directory_override_then_the_home_then_the_default()
    {
        static Func<string, string?> Read(IDictionary<string, string?> values) =>
            key => values.TryGetValue(key, out var value) ? value : null;

        Assert.Equal("/override", TraceAnalytics.Root("/default", Read(new Dictionary<string, string?> { ["SHARED_UTILITIES_TRACE_DIR"] = "/override", ["SHARED_UTILITIES_TRACE_HOME"] = "/home" })));
        Assert.Equal("/home", TraceAnalytics.Root("/default", Read(new Dictionary<string, string?> { ["SHARED_UTILITIES_TRACE_HOME"] = "/home" })));
        Assert.Equal("/default", TraceAnalytics.Root("/default", Read(new Dictionary<string, string?> { ["SHARED_UTILITIES_TRACE_DIR"] = " ", ["SHARED_UTILITIES_TRACE_HOME"] = " " })));
        Assert.Equal("/default", TraceAnalytics.Root("/default", Read(new Dictionary<string, string?>())));
    }

    // The summary fixture: three phases of one estate run and one desktop-manager run whose root timed out and whose
    // child exited with 401.
    private void TwoRuns()
    {
        TraceFixtures.Write(
            Root,
            "estate",
            "2026-03-04",
            TraceFixtures.Span("estate sync", "s1", start: 0, end: 2),
            TraceFixtures.Span("git status", "s2", parent: "s1", start: 0.1, end: 0.2, attributes: [("process.command", "git status")]),
            TraceFixtures.Span("git status", "s3", parent: "s1", start: 0.3, end: 1.3, attributes: [("process.command", "git status")]));
        TraceFixtures.Write(
            Root,
            "desktop-manager",
            "2026-03-04",
            TraceFixtures.Span(
                "rig gh pr wait",
                "r1",
                traceId: "trace-0002",
                start: 10,
                end: 13,
                status: 2,
                message: "timed out",
                attributes: [("process.command", "rig gh pr wait"), ("process.cwd", "/code/rig")]),
            TraceFixtures.Span(
                "gh pr checks",
                "r2",
                traceId: "trace-0002",
                parent: "r1",
                start: 10.5,
                end: 12,
                attributes: [("process.command", "gh pr checks"), ("process.exit_code", 1), ("process.stderr_snippet", "HTTP 401")]));
    }
}
