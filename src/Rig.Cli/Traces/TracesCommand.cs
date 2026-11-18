using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core.Observability;
using Rig.Core.Text;

namespace Rig.Cli.Traces;

/// <summary>
/// <c>rig traces</c>: inspect the OTLP-JSON day files the rig tools write. <c>runs</c> lists recent process runs and,
/// with <c>--errors-only</c>, the exact ids and process facts of each failed operation; <c>summary</c> aggregates the
/// window, its slowest operations and recent error distribution, as a table or as the JSON documented in
/// <c>docs/observability.md</c>.
/// </summary>
/// <param name="defaultRoot">This checkout's <c>temp/traces/sdk</c>.</param>
/// <param name="environment">Reads an environment variable; <see cref="TraceAnalytics.DirVariable"/> overrides the root.</param>
/// <param name="time">The clock the <c>--since</c> window is measured from.</param>
/// <param name="zone">The zone run times are shown in.</param>
/// <param name="output">Where the tables and JSON go.</param>
/// <param name="error">Where an invalid option is reported.</param>
[CliCommand(Name = "traces", Description = "Inspect the OTLP-JSON trace files under temp/traces/sdk: recent runs, failures and slow commands.")]
internal sealed class TracesCommand(
    string defaultRoot,
    Func<string, string?> environment,
    TimeProvider time,
    TimeZoneInfo zone,
    TextWriter output,
    TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
    internal TracesCommand()
        : this(string.Empty, static _ => null, TimeProvider.System, TimeZoneInfo.Utc, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>An unreadable option exits with this.</summary>
    public const int UsageExit = 2;

    private string Root => TraceAnalytics.Root(defaultRoot, environment);

    /// <summary>The <c>traces</c> command.</summary>
    public Command Build()
    {
        var traces = DeclarativeCommand.BuildGroup(this);
        traces.Add(DeclarativeCommand.Build(new RunsCommand(this)));
        traces.Add(DeclarativeCommand.Build(new SummaryCommand(this)));
        return traces;
    }

    internal int Runs(int limit, string? tool, bool errorsOnly, string? since)
    {
        if (limit < 1)
        {
            error.Write($"Invalid value for --limit: {limit.ToString(CultureInfo.InvariantCulture)} is not in the range x>=1.\n");
            return UsageExit;
        }

        DateTimeOffset? from = null;
        if (since is not null)
        {
            if (!TryWindow(since, out var window))
            {
                return UsageExit;
            }

            from = time.GetUtcNow() - window.Span;
        }

        var dataset = TraceAnalytics.Load(Root, from, tool, Skipped);
        var found = dataset.Runs.Where(run => !errorsOnly || run.Failed).ToList();
        var shown = found.Skip(Math.Max(found.Count - limit, 0)).Reverse().ToList();
        if (shown.Count == 0)
        {
            output.Write(errorsOnly
                ? $"No failed trace runs under {Root}.\n"
                : $"No trace runs under {Root}.\n");
            return 0;
        }

        var table = new TextTable(
            null,
            [("trace", false), ("tool", false), ("command", false), ("started", false), ("took", true), ("spans", true), ("errors", true)]);
        foreach (var run in shown)
        {
            table.Add(
                Short(run.TraceId),
                run.Tool,
                Clip(run.Command, 60),
                Started(run.StartNs).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Duration(run.DurationMs),
                run.Spans.Count.ToString(CultureInfo.InvariantCulture),
                run.Failures.ToString(CultureInfo.InvariantCulture));
        }

        output.Write(table.Render());
        if (errorsOnly)
        {
            Errors(shown);
        }

        return 0;
    }

    private void Errors(IReadOnlyList<TraceRunInfo> runs)
    {
        List<TraceSpanInfo> failed =
        [
            .. runs.SelectMany(run => run.Spans)
                .Where(span => span.Failed)
                .OrderByDescending(span => span.EndNs)
                .ThenByDescending(span => span.TraceId, StringComparer.Ordinal)
                .ThenByDescending(span => span.SpanId, StringComparer.Ordinal),
        ];
        output.Write($"\nFailed operations ({failed.Count.ToString(CultureInfo.InvariantCulture)}):\n");
        foreach (var span in failed)
        {
            output.Write($"  traceId={span.TraceId} spanId={span.SpanId} tool={span.Tool} span={span.Name}\n");
            output.Write($"    process.command: {OrDash(span.Command)}\n");
            output.Write($"    process.cwd: {OrDash(span.Cwd)}\n");
            output.Write($"    process.exit_code: {(span.ExitCode is { } exit ? exit.ToString(CultureInfo.InvariantCulture) : "-")}\n");
            output.Write($"    process.stderr_snippet: {span.StderrSnippet}\n");
            if (span.Error.Length > 0)
            {
                output.Write($"    error: {span.Error}\n");
            }
        }
    }

    internal int Summary(string since, bool json)
    {
        if (!TryWindow(since, out var window))
        {
            return UsageExit;
        }

        var from = time.GetUtcNow() - window.Span;
        var dataset = TraceAnalytics.Load(Root, from, skipped: Skipped);
        var summary = TraceAnalytics.Summarize(dataset);
        if (json)
        {
            output.WriteLine(Json(summary, window, from, Root).ToJsonString(JsonOutputOptions.Indented));
            return 0;
        }

        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Trace summary: last {window.Text}")
            .Append(" (since ")
            .Append(from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append(" UTC)\n")
            .Append(CultureInfo.InvariantCulture, $"  runs          {summary.Runs}\n")
            .Append(CultureInfo.InvariantCulture, $"  spans         {summary.Spans}\n")
            .Append(CultureInfo.InvariantCulture, $"  failed spans  {summary.Failures} ({Rate(summary.FailureRate)})\n")
            .Append(CultureInfo.InvariantCulture, $"  span duration p50 {Duration(summary.SpanDurations.P50)}, p90 {Duration(summary.SpanDurations.P90)}, p99 {Duration(summary.SpanDurations.P99)}\n")
            .Append(CultureInfo.InvariantCulture, $"  run duration  p50 {Duration(summary.RunDurations.P50)}, p90 {Duration(summary.RunDurations.P90)}, p99 {Duration(summary.RunDurations.P99)}\n");
        if (summary.Spans == 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"No traces under {Root} in this window.\n");
        }

        if (summary.Tools.Count > 0)
        {
            var tools = new TextTable("By tool", [("tool", false), ("runs", true), ("spans", true), ("failures", true)]);
            foreach (var item in summary.Tools)
            {
                tools.Add(item.Name, Number(item.Runs), Number(item.Spans), Number(item.Failures));
            }

            text.Append('\n').Append(tools.Render());
        }

        if (summary.Commands.Count > 0)
        {
            var commands = new TextTable("By command", [("command", false), ("runs", true), ("spans", true), ("failures", true)]);
            foreach (var item in summary.Commands)
            {
                commands.Add(Clip(item.Name, 70), Number(item.Runs), Number(item.Spans), Number(item.Failures));
            }

            text.Append('\n').Append(commands.Render());
        }

        if (summary.Errors.Count > 0)
        {
            var errors = new TextTable(
                "Recent errors",
                [("ended", false), ("trace", false), ("tool", false), ("command", false), ("exit", true), ("error", false)]);
            foreach (var item in summary.Errors)
            {
                errors.Add(
                    item.Ended(zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    Short(item.TraceId),
                    item.Tool,
                    Clip(item.Command.Length > 0 ? item.Command : item.Span, 50),
                    item.ExitCode is { } exit ? exit.ToString(CultureInfo.InvariantCulture) : "-",
                    Clip(item.Message, 70));
            }

            text.Append('\n').Append(errors.Render());
        }

        output.Write(text.ToString());
        return 0;
    }

    /// <summary>The <c>summary --json</c> payload; the schema is documented in <c>docs/observability.md</c>.</summary>
    internal static JsonObject Json(TraceSummary summary, TraceWindow window, DateTimeOffset since, string root)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(window);
        return new JsonObject
        {
            ["root"] = root,
            ["since"] = since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["window"] = window.Text,
            ["runs"] = summary.Runs,
            ["spans"] = summary.Spans,
            ["failures"] = summary.Failures,
            ["failure_rate"] = Round(summary.FailureRate, 6),
            ["durations_ms"] = Percentiles(summary.SpanDurations),
            ["run_durations_ms"] = Percentiles(summary.RunDurations),
            ["tools"] = Breakdown(summary.Tools),
            ["commands"] = Breakdown(summary.Commands),
            ["errors"] = new JsonArray([.. summary.Errors.Select(item => (JsonNode)new JsonObject
            {
                ["trace_id"] = item.TraceId,
                ["span_id"] = item.SpanId,
                ["tool"] = item.Tool,
                ["span"] = item.Span,
                ["command"] = item.Command,
                ["cwd"] = item.Cwd,
                ["exit_code"] = item.ExitCode is { } exit ? JsonValue.Create(exit) : null,
                ["status_code"] = item.StatusCode is { } status ? JsonValue.Create(status) : null,
                ["stderr"] = item.Stderr,
                ["exception"] = item.ExceptionType,
                ["exception_message"] = item.ExceptionMessage,
                ["message"] = item.Message,
            })]),
        };
    }

    private static JsonObject Percentiles(TracePercentiles values) =>
        new()
        {
            ["p50"] = Round(values.P50, 3),
            ["p90"] = Round(values.P90, 3),
            ["p99"] = Round(values.P99, 3),
        };

    private static JsonArray Breakdown(IReadOnlyList<TraceBreakdown> rows) =>
        new([.. rows.Select(row => (JsonNode)new JsonObject
        {
            ["name"] = row.Name,
            ["runs"] = row.Runs,
            ["spans"] = row.Spans,
            ["failures"] = row.Failures,
        })]);

    private void Skipped(string problem) => error.Write($"rig: skipped {problem}\n");

    private bool TryWindow(string text, out TraceWindow window)
    {
        try
        {
            window = TraceWindow.Parse(text);
            return true;
        }
        catch (FormatException problem)
        {
            error.Write($"rig: {problem.Message}\n");
            window = new TraceWindow(text, TimeSpan.Zero);
            return false;
        }
    }

    private DateTimeOffset Started(long ns) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddTicks(ns / 100), zone);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static string Rate(double value) => (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);

    private static string Duration(double milliseconds) =>
        milliseconds switch
        {
            < 1 => milliseconds.ToString("0.000", CultureInfo.InvariantCulture) + "ms",
            < 1000 => milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms",
            _ => (milliseconds / 1000).ToString("0.00", CultureInfo.InvariantCulture) + "s",
        };

    private static string Clip(string value, int width)
    {
        var one = value.ReplaceLineEndings(" ");
        return one.Length <= width ? one : one[..(width - 3)] + "...";
    }

    private static string OrDash(string value) => value.Length > 0 ? value : "-";

    /// <summary><c>rig traces runs</c>.</summary>
    [CliCommand(Name = "runs", Description = "Recent process runs, newest first: tool, command, duration, spans and failures.")]
    internal sealed class RunsCommand(TracesCommand traces)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal RunsCommand()
            : this(null!)
        {
        }

        /// <summary>How many runs to show.</summary>
        [CliOption(Name = "--limit", Description = "Only the N most recent runs.")]
        public int Limit { get; set; } = 20;

        /// <summary>Restricts to one tool.</summary>
        [CliOption(Name = "--tool", Required = false, Description = "Only spans whose service.name is this tool.")]
        public string? Tool { get; set; }

        /// <summary>Restricts to failed runs.</summary>
        [CliOption(Name = "--errors-only", Description = "Only runs with a failed span, and print each failure's ids, command, cwd, exit code and stderr snippet.")]
        public bool ErrorsOnly { get; set; }

        /// <summary>The window.</summary>
        [CliOption(Name = "--since", Required = false, Description = "Only runs with a span since this window, e.g. 24h or 7d. Default: all recorded.")]
        public string? Since { get; set; }

        /// <summary>Lists the runs.</summary>
        public int Run() => traces.Runs(Limit, Tool, ErrorsOnly, Since);
    }

    /// <summary><c>rig traces summary</c>.</summary>
    [CliCommand(Name = "summary", Description = "Totals, failure rate, p50/p90/p99 durations, breakdowns by tool and command, and recent errors.")]
    internal sealed class SummaryCommand(TracesCommand traces)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal SummaryCommand()
            : this(null!)
        {
        }

        /// <summary>The window.</summary>
        [CliOption(Name = "--since", Description = "How far back to aggregate, e.g. 24h or 7d.")]
        public string Since { get; set; } = "24h";

        /// <summary>Emits JSON.</summary>
        [CliOption(Name = "--json", Description = "Machine-readable output; the schema is documented in docs/observability.md.")]
        public bool Json { get; set; }

        /// <summary>Summarises the window.</summary>
        public int Run() => traces.Summary(Since, Json);
    }
}
