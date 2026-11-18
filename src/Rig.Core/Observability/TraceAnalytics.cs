using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Otel;
using Rig.Core.Text;
using Rig.Core.Vault;

namespace Rig.Core.Observability;

/// <summary>An event recorded on a span, such as an exception.</summary>
/// <param name="Name">The event's name.</param>
/// <param name="AtNs">When it happened, in Unix nanoseconds.</param>
/// <param name="Attributes">Its attributes.</param>
public sealed record TraceSpanEvent(string Name, long AtNs, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>One span read from a trace file, with the process facts diagnostics need.</summary>
/// <param name="TraceId">The span's trace.</param>
/// <param name="SpanId">The span's id within it.</param>
/// <param name="ParentSpanId">Its parent's id, empty for a root.</param>
/// <param name="Tool">The <c>service.name</c> it was written under.</param>
/// <param name="Scope">The instrumentation scope's name.</param>
/// <param name="Name">The span's name.</param>
/// <param name="StartNs">When it started, in Unix nanoseconds.</param>
/// <param name="EndNs">When it ended, in Unix nanoseconds.</param>
/// <param name="StatusCode">The OTLP status code; null when the span has none.</param>
/// <param name="StatusMessage">The status's message, empty when absent.</param>
/// <param name="ExitCode">The traced process's exit code, when the span carried one.</param>
/// <param name="Command">The traced process's command line, when the span carried one.</param>
/// <param name="Cwd">The traced process's working directory, when the span carried one.</param>
/// <param name="StderrSnippet">The stderr snippet a failed process recorded.</param>
/// <param name="Attributes">Every attribute the span carried.</param>
/// <param name="Events">Its events, in order.</param>
public sealed record TraceSpanInfo(
    string TraceId,
    string SpanId,
    string ParentSpanId,
    string Tool,
    string Scope,
    string Name,
    long StartNs,
    long EndNs,
    int? StatusCode,
    string StatusMessage,
    long? ExitCode,
    string Command,
    string Cwd,
    string StderrSnippet,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<TraceSpanEvent> Events)
{
    /// <summary>How long it took in milliseconds; never negative.</summary>
    public double DurationMs => Math.Max(EndNs - StartNs, 0) / 1_000_000.0;

    /// <summary>It was flushed before it ended (<c>span.incomplete</c>).</summary>
    public bool Incomplete => Attributes.GetValueOrDefault("span.incomplete") is true;

    /// <summary>
    /// Whether it failed: a status code other than 1, or a traced process whose exit code was not 0.
    /// </summary>
    public bool Failed => (StatusCode is { } code && code != 1) || (ExitCode is { } exit && exit != 0);

    /// <summary>The most specific failure message: the status, else the exception, else the stderr snippet.</summary>
    public string Error =>
        StatusMessage.Length > 0 ? StatusMessage
        : ExceptionMessage.Length > 0 ? ExceptionMessage
        : StderrSnippet;

    /// <summary>The first <c>exception</c> event's type, empty when there is none.</summary>
    public string ExceptionType =>
        Events.FirstOrDefault(item => item.Name == "exception") is { } found
            ? Text(found.Attributes.GetValueOrDefault("exception.type"))
            : string.Empty;

    /// <summary>The first <c>exception</c> event's message, empty when there is none.</summary>
    public string ExceptionMessage =>
        Events.FirstOrDefault(item => item.Name == "exception") is { } found
            ? Text(found.Attributes.GetValueOrDefault("exception.message"))
            : string.Empty;

    private static string Text(object? value) => value as string ?? string.Empty;
}

/// <summary>One process run: every span sharing a trace id.</summary>
/// <param name="TraceId">The trace's id.</param>
/// <param name="Tool">The root span's tool, else the earliest span's.</param>
/// <param name="Name">The root span's name, else the earliest span's.</param>
/// <param name="Command">The root span's process command, else its name.</param>
/// <param name="StartNs">The earliest span's start, in Unix nanoseconds.</param>
/// <param name="EndNs">The latest span's end, in Unix nanoseconds.</param>
/// <param name="Spans">The run's spans, earliest first.</param>
public sealed record TraceRunInfo(
    string TraceId,
    string Tool,
    string Name,
    string Command,
    long StartNs,
    long EndNs,
    IReadOnlyList<TraceSpanInfo> Spans)
{
    /// <summary>How long the run took in milliseconds; never negative.</summary>
    public double DurationMs => Math.Max(EndNs - StartNs, 0) / 1_000_000.0;

    /// <summary>It has at least one failed span.</summary>
    public bool Failed => Spans.Any(span => span.Failed);

    /// <summary>How many of its spans failed.</summary>
    public int Failures => Spans.Count(span => span.Failed);
}

/// <summary>What one tool or command did in a window.</summary>
/// <param name="Name">The tool or command.</param>
/// <param name="Runs">Distinct traces it appeared in.</param>
/// <param name="Spans">Spans it produced.</param>
/// <param name="Failures">Spans that failed.</param>
public sealed record TraceBreakdown(string Name, int Runs, int Spans, int Failures);

/// <summary>A recent failed operation, with everything needed to reproduce it.</summary>
/// <param name="TraceId">Its trace.</param>
/// <param name="SpanId">Its span.</param>
/// <param name="Tool">The tool that recorded it.</param>
/// <param name="Span">Its name.</param>
/// <param name="Command">The process command line, when it carried one.</param>
/// <param name="Cwd">The process working directory, when it carried one.</param>
/// <param name="ExitCode">The process exit code, when it carried one.</param>
/// <param name="StatusCode">The OTLP status code, when it carried one.</param>
/// <param name="StatusMessage">The OTLP status message, when it carried one.</param>
/// <param name="Stderr">The recorded stderr snippet.</param>
/// <param name="ExceptionType">The exception type, when the span recorded one.</param>
/// <param name="ExceptionMessage">The exception message, when the span recorded one.</param>
/// <param name="EndNs">When it ended, in Unix nanoseconds.</param>
public sealed record TraceError(
    string TraceId,
    string SpanId,
    string Tool,
    string Span,
    string Command,
    string Cwd,
    long? ExitCode,
    int? StatusCode,
    string StatusMessage,
    string Stderr,
    string ExceptionType,
    string ExceptionMessage,
    long EndNs)
{
    /// <summary>The most specific message: the status, the exception, then the stderr snippet.</summary>
    public string Message =>
        StatusMessage.Length > 0 ? StatusMessage : ExceptionMessage.Length > 0 ? ExceptionMessage : Stderr;

    /// <summary>When it ended, in <paramref name="zone"/>.</summary>
    public DateTimeOffset Ended(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddTicks(EndNs / 100), zone);
    }
}

/// <summary>Percentiles in milliseconds, nearest-rank.</summary>
/// <param name="P50">The median.</param>
/// <param name="P90">The 90th percentile.</param>
/// <param name="P99">The 99th percentile.</param>
public sealed record TracePercentiles(double P50, double P90, double P99);

/// <summary>Everything a window of trace files holds.</summary>
/// <param name="Runs">The runs, oldest first.</param>
/// <param name="Spans">Every distinct span in the window, in file order.</param>
/// <param name="Files">The trace files that were read.</param>
public sealed record TraceDataset(IReadOnlyList<TraceRunInfo> Runs, IReadOnlyList<TraceSpanInfo> Spans, IReadOnlyList<string> Files);

/// <summary>Aggregates over a window of traces.</summary>
/// <param name="Runs">How many process runs.</param>
/// <param name="Spans">How many distinct spans.</param>
/// <param name="Failures">How many spans failed.</param>
/// <param name="FailureRate">Failed spans over all spans; 0 when there are none.</param>
/// <param name="SpanDurations">Span duration percentiles.</param>
/// <param name="RunDurations">Run duration percentiles.</param>
/// <param name="Tools">Totals by tool, commonest first.</param>
/// <param name="Commands">Totals by command, commonest first.</param>
/// <param name="Errors">Recent failed operations, newest first.</param>
public sealed record TraceSummary(
    int Runs,
    int Spans,
    int Failures,
    double FailureRate,
    TracePercentiles SpanDurations,
    TracePercentiles RunDurations,
    IReadOnlyList<TraceBreakdown> Tools,
    IReadOnlyList<TraceBreakdown> Commands,
    IReadOnlyList<TraceError> Errors);

/// <summary>A window such as <c>24h</c> or <c>7d</c>.</summary>
/// <param name="Text">The window as written.</param>
/// <param name="Span">How far back it reaches.</param>
public sealed partial record TraceWindow(string Text, TimeSpan Span)
{
    /// <summary>
    /// Parses <c>30s</c>, <c>15m</c>, <c>24h</c>, <c>7d</c> or <c>2w</c>. Throws <see cref="FormatException"/> for
    /// anything else, or a window of zero.
    /// </summary>
    public static TraceWindow Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Shape().Match(text.Trim());
        if (!match.Success
            || !long.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count <= 0)
        {
            throw new FormatException($"Invalid window '{text}'. Use a value like 24h or 7d.");
        }

        var span = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "s" => TimeSpan.FromSeconds(count),
            "m" => TimeSpan.FromMinutes(count),
            "h" => TimeSpan.FromHours(count),
            "d" => TimeSpan.FromDays(count),
            _ => TimeSpan.FromDays(count * 7),
        };
        return new TraceWindow(text.Trim(), span);
    }

    [GeneratedRegex(@"^(?<count>[0-9]+)(?<unit>[smhdw])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}

/// <summary>
/// Reads the OTLP-JSON trace files rig's tools write and aggregates the failures, slow commands and error
/// distribution an agent needs to diagnose a background session. The files are parsed as a JSON stream: no embedded
/// DuckDB is needed, and a malformed line or file is skipped rather than failing the report.
/// </summary>
public static partial class TraceAnalytics
{
    /// <summary>Overrides where the tracer writes, as <see cref="TraceFileWriter.DirVariable"/> names it.</summary>
    public const string DirVariable = TraceFileWriter.DirVariable;

    /// <summary>Replaces the default root only, as <see cref="TraceFileWriter.HomeVariable"/> names it.</summary>
    public const string HomeVariable = TraceFileWriter.HomeVariable;

    /// <summary>How many recent errors a summary carries unless told otherwise.</summary>
    public const int DefaultErrorLimit = 20;

    /// <summary>
    /// The trace folder: <see cref="DirVariable"/> when set, else <see cref="HomeVariable"/> when set, else
    /// <paramref name="defaultRoot"/>, matching where <see cref="TraceFileWriter"/> writes.
    /// </summary>
    public static string Root(string defaultRoot, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(defaultRoot);
        ArgumentNullException.ThrowIfNull(environment);
        var over = environment(DirVariable);
        if (!string.IsNullOrWhiteSpace(over))
        {
            return over;
        }

        var home = environment(HomeVariable);
        return string.IsNullOrWhiteSpace(home) ? defaultRoot : home;
    }

    /// <summary>
    /// Every distinct span under <paramref name="root"/> that ended at or after <paramref name="since"/>, grouped into
    /// runs. A file whose day is older than the window is not read; a file not named for a day is read when it was
    /// modified inside the window. A missing or unreadable folder gives an empty dataset. When <paramref name="tool"/>
    /// is given only that tool's spans are kept.
    /// </summary>
    public static TraceDataset Load(string root, DateTimeOffset? since = null, string? tool = null, Action<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        var files = Files(root, since);
        var sinceNs = since is { } start ? (start.UtcDateTime - DateTime.UnixEpoch).Ticks * 100 : (long?)null;
        var chosen = new OrderedDictionary<(string TraceId, string SpanId), TraceSpanInfo>();
        foreach (var path in files)
        {
            var fallbackTool = ToolFromPath(path, root);
            foreach (var file in TraceSpans.Read(path, skipped))
            {
                if (View(file, fallbackTool, skipped) is not { } span)
                {
                    continue;
                }

                var key = (span.TraceId, span.SpanId);
                if (!chosen.TryGetValue(key, out var held) || held.Incomplete || !span.Incomplete)
                {
                    chosen[key] = span;
                }
            }
        }

        var spans = chosen.Values
            .Where(span => sinceNs is null || span.EndNs >= sinceNs)
            .Where(span => string.IsNullOrEmpty(tool) || span.Tool.Equals(tool, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new TraceDataset(Runs(spans), spans, files);
    }

    /// <summary>Aggregates a dataset: totals, failure rate, duration percentiles, breakdowns and recent errors.</summary>
    public static TraceSummary Summarize(TraceDataset dataset, int errorLimit = DefaultErrorLimit)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentOutOfRangeException.ThrowIfNegative(errorLimit);
        var failures = dataset.Spans.Count(span => span.Failed);
        return new TraceSummary(
            dataset.Runs.Count,
            dataset.Spans.Count,
            failures,
            dataset.Spans.Count == 0 ? 0 : (double)failures / dataset.Spans.Count,
            Percentiles(dataset.Spans.Select(span => span.DurationMs)),
            Percentiles(dataset.Runs.Select(run => run.DurationMs)),
            Breakdown(dataset.Spans, span => span.Tool),
            Breakdown(dataset.Spans, Command),
            Errors(dataset.Spans, errorLimit));
    }

    /// <summary>The <paramref name="limit"/> newest failed spans, newest first.</summary>
    public static IReadOnlyList<TraceError> Errors(IEnumerable<TraceSpanInfo> spans, int limit = DefaultErrorLimit)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return
        [
            .. spans.Where(span => span.Failed)
                .OrderByDescending(span => span.EndNs)
                .ThenByDescending(span => span.TraceId, StringComparer.Ordinal)
                .ThenByDescending(span => span.SpanId, StringComparer.Ordinal)
                .Take(limit)
                .Select(span => new TraceError(
                    span.TraceId,
                    span.SpanId,
                    span.Tool,
                    span.Name,
                    span.Command,
                    span.Cwd,
                    span.ExitCode,
                    span.StatusCode,
                    span.StatusMessage,
                    span.StderrSnippet,
                    span.ExceptionType,
                    span.ExceptionMessage,
                    span.EndNs)),
        ];
    }

    /// <summary>The command a span ran: its process command, else its name, else <c>unknown</c>.</summary>
    public static string Command(TraceSpanInfo span)
    {
        ArgumentNullException.ThrowIfNull(span);
        return span.Command.Length > 0 ? span.Command : span.Name.Length > 0 ? span.Name : "unknown";
    }

    /// <summary>Nearest-rank percentiles of <paramref name="values"/>; all zero when empty.</summary>
    public static TracePercentiles Percentiles(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var ordered = values.Order().ToList();
        return ordered.Count == 0 ? new TracePercentiles(0, 0, 0) : new TracePercentiles(Rank(ordered, 0.50), Rank(ordered, 0.90), Rank(ordered, 0.99));

        static double Rank(List<double> ordered, double quantile)
        {
            var rank = Math.Max(1, (int)Math.Ceiling(quantile * ordered.Count));
            return ordered[Math.Min(ordered.Count, rank) - 1];
        }
    }

    // The files that can hold spans in the window: day files by their name, anything else by mtime.
    private static IReadOnlyList<string> Files(string root, DateTimeOffset? since)
    {
        var directory = new DirectoryInfo(root);
        if (!directory.Exists)
        {
            return [];
        }

        List<FileInfo> candidates;
        try
        {
            candidates = [.. directory.EnumerateFiles("*.ndjson", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true })];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var cutoff = since is { } start ? DateOnly.FromDateTime(start.UtcDateTime).AddDays(-1) : (DateOnly?)null;
        var kept = new List<string>();
        foreach (var candidate in candidates)
        {
            if (cutoff is null)
            {
                kept.Add(candidate.FullName);
                continue;
            }

            var match = DayFile().Match(candidate.Name);
            if (match.Success
                && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                if (day >= cutoff)
                {
                    kept.Add(candidate.FullName);
                }

                continue;
            }

            try
            {
                if (candidate.LastWriteTimeUtc >= since!.Value.UtcDateTime)
                {
                    kept.Add(candidate.FullName);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Gone while we looked; nothing to read.
            }
        }

        return [.. kept.Order(UnicodeText.CodePointOrder)];
    }

    private static IReadOnlyList<TraceRunInfo> Runs(IReadOnlyList<TraceSpanInfo> spans)
    {
        List<TraceRunInfo> runs = [];
        foreach (var group in spans.GroupBy(span => span.TraceId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(span => span.StartNs).ThenBy(span => span.SpanId, StringComparer.Ordinal).ToList();
            var root = ordered.FirstOrDefault(span => span.ParentSpanId.Length == 0) ?? ordered[0];
            runs.Add(new TraceRunInfo(
                group.Key,
                root.Tool,
                root.Name,
                Command(root),
                ordered.Min(span => span.StartNs),
                ordered.Max(span => span.EndNs),
                ordered));
        }

        return [.. runs.OrderBy(run => run.StartNs).ThenBy(run => run.TraceId, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<TraceBreakdown> Breakdown(IReadOnlyList<TraceSpanInfo> spans, Func<TraceSpanInfo, string> name)
    {
        List<TraceBreakdown> rows = [];
        foreach (var group in spans.GroupBy(name, StringComparer.Ordinal))
        {
            rows.Add(new TraceBreakdown(
                group.Key.Length > 0 ? group.Key : "unknown",
                group.Select(span => span.TraceId).Distinct(StringComparer.Ordinal).Count(),
                group.Count(),
                group.Count(span => span.Failed)));
        }

        return [.. rows.OrderByDescending(row => row.Spans).ThenByDescending(row => row.Failures).ThenBy(row => row.Name, StringComparer.Ordinal)];
    }

    // The tool a file was written under when the resource does not name one: the folder below the traces root.
    private static string ToolFromPath(string path, string root)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        var rootPath = Path.GetFullPath(root);
        if (folder is null)
        {
            return "unknown";
        }

        var relative = Path.GetRelativePath(rootPath, folder);
        return relative is "." or "" || relative.StartsWith("..", StringComparison.Ordinal)
            ? "unknown"
            : relative.Replace('\\', '/').Split('/')[0];
    }

    // A span with a field of the wrong type is skipped and reported, naming the file, line and JSON path.
    private static TraceSpanInfo? View(FileSpan file, string fallbackTool, Action<string>? skipped)
    {
        try
        {
            return View(file, fallbackTool);
        }
        catch (JsonInputException e)
        {
            skipped?.Invoke(e.Under(file.Origin, file.SpanPath).Message);
            return null;
        }
    }

    private static TraceSpanInfo View(FileSpan file, string fallbackTool)
    {
        var raw = file.Span;
        var attributes = OtlpJson.Attributes(raw, "attributes");
        var start = OtlpJson.Int64(raw["startTimeUnixNano"]) ?? 0;
        var end = OtlpJson.Int64(raw["endTimeUnixNano"]) ?? start;
        var (statusCode, statusMessage) = Status(raw);
        var tool = file.Resource is JsonObject resObj
            ? TextValue(OtlpJson.Attributes(resObj, "attributes").GetValueOrDefault("service.name"))
            : string.Empty;
        var scope = file.Scope is JsonObject scObj ? Text(scObj, "name") : string.Empty;
        return new TraceSpanInfo(
            file.TraceId,
            file.SpanId,
            Text(raw, "parentSpanId"),
            tool.Length > 0 ? tool : fallbackTool,
            scope,
            Text(raw, "name"),
            start,
            end < start ? start : end,
            statusCode,
            statusMessage,
            ExitCode(attributes, "process.exit_code") ?? ExitCode(attributes, "process.exit.code"),
            TextValue(attributes.GetValueOrDefault("process.command")) is { Length: > 0 } command
                ? command
                : TextValue(attributes.GetValueOrDefault("process.command_args")),
            TextValue(attributes.GetValueOrDefault("process.cwd")),
            TextValue(attributes.GetValueOrDefault("process.stderr_snippet")),
            attributes,
            Events(raw));
    }

    private static List<TraceSpanEvent> Events(JsonObject raw)
    {
        var items = OtlpJson.Items(raw, "events");
        List<TraceSpanEvent> events = [];
        for (var index = 0; index < items.Count; index++)
        {
            var found = items[index] as JsonObject ?? throw JsonProblem.Expected(JsonProblem.Item(items.GetPath(), index), "an object", JsonProblem.Describe(items[index]));
            events.Add(new TraceSpanEvent(Text(found, "name"), OtlpJson.Int64(found["timeUnixNano"]) ?? 0, OtlpJson.Attributes(found, "attributes")));
        }

        return events;
    }

    // OTLP status codes are 0, 1 or 2 as numbers, or the enum's name; only an error code and its message matter here.
    private static (int? Code, string Message) Status(JsonObject raw)
    {
        if (raw["status"] is not { } node)
        {
            return (null, string.Empty);
        }

        var status = node as JsonObject ?? throw JsonProblem.Expected(node, "an object");
        var message = Text(status, "message");
        return (status["code"] switch
        {
            null => null,
            JsonValue word when word.GetValueKind() == JsonValueKind.String => StatusName(word),
            JsonValue { } number when number.GetValueKind() == JsonValueKind.Number && number.TryGetValue<int>(out var code) => code,
            var other => throw JsonProblem.Expected(other, StatusExpected),
        }, message);
    }

    private const string StatusExpected = "a whole number or STATUS_CODE_ERROR, STATUS_CODE_OK or STATUS_CODE_UNSET";

    private static int? StatusName(JsonValue name) => name.GetValue<string>() switch
    {
        "Error" or "STATUS_CODE_ERROR" => 2,
        "STATUS_CODE_OK" or "STATUS_CODE_UNSET" => null,
        _ => throw JsonProblem.Expected(name, StatusExpected),
    };

    private static long? ExitCode(IReadOnlyDictionary<string, object?> attributes, string name) =>
        attributes.GetValueOrDefault(name) switch
        {
            null => null,
            long code => code,
            var other => throw JsonProblem.Expected("$.attributes", $"{name} to be a whole number", JsonProblem.Describe(other)),
        };

    // A string field; absent or null reads as empty.
    private static string Text(JsonObject owner, string key) =>
        owner[key] switch
        {
            null => string.Empty,
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            var other => throw JsonProblem.Expected(owner, key, "a string", other),
        };

    private static string TextValue(object? value) => value as string ?? string.Empty;

    [GeneratedRegex(@"^([0-9]{4}-[0-9]{2}-[0-9]{2})\.ndjson$", RegexOptions.CultureInvariant)]
    private static partial Regex DayFile();
}
