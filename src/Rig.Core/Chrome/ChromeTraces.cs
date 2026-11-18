using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Otel;
using Rig.Core.Text;
using Rig.Core.Vault;

namespace Rig.Core.Chrome;

/// <summary>One day's trace file.</summary>
/// <param name="Day">The day in its name.</param>
/// <param name="Path">Its path.</param>
public sealed record TraceFile(DateOnly Day, string Path);

/// <summary>An event on a span.</summary>
/// <param name="Name">The event's name.</param>
/// <param name="AtNs">When it happened, in Unix nanoseconds.</param>
/// <param name="Attributes">Its attributes: strings, bools, longs, doubles, lists of those, or null.</param>
public sealed record TraceEvent(string Name, long AtNs, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>One span read back from a trace file.</summary>
/// <param name="TraceId">Its trace.</param>
/// <param name="SpanId">Its id.</param>
/// <param name="ParentId">Its parent's id; empty for a root.</param>
/// <param name="Name">Its name.</param>
/// <param name="StartNs">When it started, in Unix nanoseconds.</param>
/// <param name="EndNs">When it ended, in Unix nanoseconds.</param>
/// <param name="Error">The error status's message; empty when it did not fail.</param>
/// <param name="Attributes">Its attributes.</param>
/// <param name="Events">Its events, in order.</param>
public sealed record TraceSpan(
    string TraceId,
    string SpanId,
    string ParentId,
    string Name,
    long StartNs,
    long EndNs,
    string Error,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<TraceEvent> Events)
{
    /// <summary>How long it took; never negative.</summary>
    public double Seconds => Math.Max(EndNs - StartNs, 0) / 1e9;

    /// <summary>It was flushed before it ended.</summary>
    public bool Incomplete => ChromeTraces.Bool(Attributes, "span.incomplete") is true;
}

/// <summary>One organise run: its root span and every span beneath it.</summary>
/// <param name="Root">The <see cref="ChromeTraces.RunSpan"/> span.</param>
/// <param name="Spans">Every span beneath it.</param>
public sealed record TraceRun(TraceSpan Root, IReadOnlyList<TraceSpan> Spans)
{
    private const string WaitSpan = "chrome_tabs.wait_for_idle";

    /// <summary>The first eight characters of the root's span id.</summary>
    public string Id => Root.SpanId.Length > 8 ? Root.SpanId[..8] : Root.SpanId;

    /// <summary>What started it: <c>scheduled</c>, <c>manual</c>…</summary>
    public string? Trigger => ChromeTraces.Str(Root.Attributes, "chrome.trigger");

    /// <summary>Nobody asked for it.</summary>
    public bool? Unattended => ChromeTraces.Bool(Root.Attributes, "chrome.unattended");

    /// <summary>Tabs it closed.</summary>
    public long? TabsClosed => ChromeTraces.Int(Root.Attributes, "tabs.closed");

    /// <summary>Moves grouping planned.</summary>
    public long? GroupPlanned => ChromeTraces.Int(Root.Attributes, "group.planned");

    /// <summary>Tabs grouping moved.</summary>
    public long? GroupMoved => ChromeTraces.Int(Root.Attributes, "group.moved");

    /// <summary>The last reason the wait for idle saw.</summary>
    public string? LastBlock => ChromeTraces.Str(Root.Attributes, "wait.last_block");

    /// <summary><c>incomplete</c> when it was killed, <c>error</c> when it failed, else its <c>run.outcome</c> or <c>ok</c>.</summary>
    public string Outcome =>
        Root.Incomplete ? "incomplete"
        : Root.Error.Length > 0 ? "error"
        : ChromeTraces.Str(Root.Attributes, "run.outcome") is { Length: > 0 } outcome ? outcome
        : "ok";

    /// <summary>How grouping went, or <c>-</c>.</summary>
    public string Grouping => ChromeTraces.Str(Root.Attributes, "group.outcome") is { Length: > 0 } grouping ? grouping : "-";

    /// <summary>How long it waited for idle: the root's <c>wait.seconds</c>, else the wait span's length.</summary>
    public double? WaitedSeconds =>
        Root.Attributes.ContainsKey("wait.seconds")
            ? ChromeTraces.Float(Root.Attributes, "wait.seconds")
            : Spans.FirstOrDefault(span => span.Name == WaitSpan)?.Seconds;

    /// <summary>How many times the wait looked; 0 when unknown.</summary>
    public long Checks => ChromeTraces.Int(Root.Attributes, "wait.checks") ?? 0;

    /// <summary>Every block reason the wait saw, in order.</summary>
    public IReadOnlyList<string> Blocks =>
    [
        .. Spans.Where(span => span.Name == WaitSpan)
            .SelectMany(span => span.Events)
            .Where(check => check.Name == "check" && ChromeTraces.Bool(check.Attributes, "blocked") is true)
            .Select(check => ChromeTraces.Str(check.Attributes, "reason") ?? string.Empty),
    ];

    /// <summary>When it started, in <paramref name="zone"/>.</summary>
    public DateTimeOffset Started(TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch.AddTicks(Root.StartNs / 100), zone);

    /// <summary>The spans directly beneath <paramref name="parent"/>, by start.</summary>
    public IReadOnlyList<TraceSpan> Children(TraceSpan parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return [.. Spans.Where(span => span.ParentId == parent.SpanId).OrderBy(span => span.StartNs)];
    }
}

/// <summary>How many runs one block reason, numbers folded, stopped.</summary>
/// <param name="Reason">The reason, with every number as <c>N</c>.</param>
/// <param name="Count">How many times it was seen.</param>
public sealed record BlockCount(string Reason, int Count);

/// <summary>Runs aggregated for <c>chrome-tabs traces summary</c>.</summary>
/// <param name="Runs">How many runs.</param>
/// <param name="Completed">Runs that finished.</param>
/// <param name="Ok">Finished runs that went to plan.</param>
/// <param name="Incomplete">Runs killed before they finished.</param>
/// <param name="Grouping">Runs by grouping verdict, in first-seen order.</param>
/// <param name="Waits">How long each run that waited waited.</param>
/// <param name="Durations">How long each finished run took.</param>
/// <param name="Closed">Tabs closed over every run.</param>
/// <param name="Blocks">The five commonest block reasons.</param>
public sealed record TraceSummary(
    int Runs,
    int Completed,
    int Ok,
    int Incomplete,
    IReadOnlyList<KeyValuePair<string, int>> Grouping,
    IReadOnlyList<double> Waits,
    IReadOnlyList<double> Durations,
    long Closed,
    IReadOnlyList<BlockCount> Blocks)
{
    /// <summary>Ok runs over finished ones; null when none finished.</summary>
    public double? SuccessRate => Completed > 0 ? (double)Ok / Completed : null;
}

/// <summary>
/// Organise-run traces: where they land, how long they are kept, and the reports read from them. Every run is one
/// <see cref="RunSpan"/> span with a child span per phase, written as OTLP/JSON lines to
/// <c>%LOCALAPPDATA%\shared-utilities\chrome-tabs\traces\&lt;YYYY-MM-DD&gt;.ndjson</c>. Pure file work, so the reports
/// run anywhere.
/// </summary>
public static partial class ChromeTraces
{
    /// <summary>The instrumentation scope the run's spans are written under.</summary>
    public const string Scope = "desktop_manager.chrome";

    /// <summary>The root span of every organise run.</summary>
    public const string RunSpan = "chrome_tabs.organise";

    /// <summary>Days of trace files kept.</summary>
    public const int KeepDays = 30;

    /// <summary>How often a run's spans are flushed, in seconds.</summary>
    public const double FlushIntervalSeconds = 30.0;

    private const string PruneMarker = ".last-prune";
    private const int OtlpStatusError = 2;
    private static readonly HashSet<string> HiddenAttributes = ["cli.argv", "span.incomplete"];
    private static readonly HashSet<string> HiddenEventAttributes = ["exception.stacktrace"];

    /// <summary>The traces folder, under <see cref="ChromeInstall.Root"/>.</summary>
    public static string TracesDir(Func<string, string?> environment, string home) =>
        Path.Join(ChromeInstall.Root(environment, home), "traces");

    /// <summary>The day files in <paramref name="directory"/>, oldest first; anything not named <c>&lt;YYYY-MM-DD&gt;.ndjson</c> is ignored.</summary>
    public static IReadOnlyList<TraceFile> TraceFiles(string directory)
    {
        List<TraceFile> found = [];
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var match = DayFile().Match(Path.GetFileName(path));
                if (match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                {
                    found.Add(new TraceFile(day, path));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return [.. found.OrderBy(file => file.Day).ThenBy(file => file.Path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Deletes day files more than <paramref name="keepDays"/> old, by the date in the name; returns the files removed,
    /// or with <paramref name="dryRun"/> the files due.
    /// </summary>
    public static IReadOnlyList<string> Prune(string directory, DateOnly today, int keepDays = KeepDays, bool dryRun = false)
    {
        var cutoff = today.AddDays(-keepDays);
        var due = TraceFiles(directory).Where(file => file.Day < cutoff).Select(file => file.Path).ToList();
        if (dryRun)
        {
            return due;
        }

        List<string> removed = [];
        foreach (var path in due)
        {
            try
            {
                File.Delete(path);
                removed.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary><see cref="Prune"/>, at most once a day: null when today's prune has already run. Never throws.</summary>
    public static IReadOnlyList<string>? PruneDaily(string directory, DateOnly today)
    {
        var marker = Path.Join(directory, PruneMarker);
        var stamp = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            if (File.Exists(marker) && UnicodeText.Strip(File.ReadAllText(marker)) == stamp)
            {
                return null;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(marker, stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Prune(directory, today);
    }

    /// <summary>
    /// Spans from OTLP/JSON lines. A complete span replaces an earlier incomplete copy; bad lines and spans are skipped, each
    /// reported to <paramref name="skipped"/> as <c>source line N: $.json.path: expected X, found Y</c>.
    /// </summary>
    public static IReadOnlyList<TraceSpan> ParseLines(IEnumerable<string> lines, Action<string>? skipped = null, string source = "trace lines")
    {
        ArgumentNullException.ThrowIfNull(lines);
        return Choose(TraceSpans.Parse(string.Join("\n", lines), skipped, source), skipped);
    }

    /// <summary>
    /// Every span in the day files of the last <paramref name="days"/> days; every file when null. Skips are reported to
    /// <paramref name="skipped"/> as <see cref="ParseLines"/> does, naming the file.
    /// </summary>
    public static IReadOnlyList<TraceSpan> LoadSpans(string directory, DateOnly today, int? days = null, Action<string>? skipped = null)
    {
        var since = days is { } window ? today.AddDays(-window) : DateOnly.MinValue;
        List<FileSpan> files = [];
        foreach (var file in TraceFiles(directory).Where(file => file.Day >= since))
        {
            try
            {
                files.AddRange(TraceSpans.Parse(File.ReadAllText(file.Path, Encoding.UTF8), skipped, file.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return Choose(files, skipped);
    }

    private static List<TraceSpan> Choose(IEnumerable<FileSpan> files, Action<string>? skipped)
    {
        var chosen = new OrderedDictionary<(string, string), TraceSpan>();
        foreach (var file in files)
        {
            if (Span(file, skipped) is not { } span)
            {
                continue;
            }

            var key = (span.TraceId, span.SpanId);
            if (!chosen.TryGetValue(key, out var held) || held.Incomplete || !span.Incomplete)
            {
                chosen[key] = span;
            }
        }

        return [.. chosen.Values];
    }

    /// <summary>Organise runs, oldest first, each with its descendant spans.</summary>
    public static IReadOnlyList<TraceRun> Runs(IReadOnlyList<TraceSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var byParent = new Dictionary<(string, string), List<TraceSpan>>();
        foreach (var span in spans)
        {
            var key = (span.TraceId, span.ParentId);
            if (!byParent.TryGetValue(key, out var children))
            {
                byParent[key] = children = [];
            }

            children.Add(span);
        }

        List<TraceRun> found = [];
        foreach (var root in spans.Where(span => span.Name == RunSpan))
        {
            List<TraceSpan> below = [];
            Stack<TraceSpan> stack = new([root]);
            while (stack.Count > 0)
            {
                foreach (var child in byParent.GetValueOrDefault((root.TraceId, stack.Pop().SpanId), []))
                {
                    below.Add(child);
                    stack.Push(child);
                }
            }

            found.Add(new TraceRun(root, below));
        }

        return [.. found.OrderBy(run => run.Root.StartNs)];
    }

    /// <summary>A run's headline facts, for <c>--json</c>.</summary>
    public static JsonObject RunJson(TraceRun run, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new JsonObject
        {
            ["id"] = run.Id,
            ["trace_id"] = run.Root.TraceId,
            ["span_id"] = run.Root.SpanId,
            ["started"] = IsoTime.Seconds(run.Started(zone)),
            ["seconds"] = Math.Round(run.Root.Seconds, 2, MidpointRounding.ToEven),
            ["trigger"] = run.Trigger,
            ["unattended"] = run.Unattended,
            ["outcome"] = run.Outcome,
            ["grouping"] = run.Grouping,
            ["tabs_closed"] = run.TabsClosed,
            ["group_planned"] = run.GroupPlanned,
            ["group_moved"] = run.GroupMoved,
            ["waited_seconds"] = run.WaitedSeconds,
            ["checks"] = run.Checks,
            ["last_block"] = run.LastBlock,
            ["blocks"] = new JsonArray([.. run.Blocks.Select(block => (JsonNode?)block)]),
            ["error"] = run.Root.Error.Length > 0 ? run.Root.Error : null,
        };
    }

    /// <summary>The latest run whose span id starts with <paramref name="wanted"/>, or the latest run when null.</summary>
    public static TraceRun? FindRun(IReadOnlyList<TraceRun> found, string? wanted)
    {
        ArgumentNullException.ThrowIfNull(found);
        return wanted is null
            ? found.Count > 0 ? found[^1] : null
            : found.Reverse().FirstOrDefault(run => run.Root.SpanId.StartsWith(UnicodeText.Lower(wanted), StringComparison.Ordinal));
    }

    /// <summary>The header over <see cref="RenderRuns"/>.</summary>
    public static string RunsHeader { get; } =
        $"{"run",-8}  {"started",-16}  {"took",6}  {"trigger",-9}  {"outcome",-10}  {"closed",6}  " +
        $"{"grouping",-15}  {"waited",6}  {"checks",6}  last block";

    /// <summary>One line per run, newest first.</summary>
    public static IReadOnlyList<string> RenderRuns(IReadOnlyList<TraceRun> found, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(found);
        return
        [
            .. found.Reverse().Select(run =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{run.Id,-8}  {run.Started(zone):yyyy-MM-dd HH:mm}  {Duration(run.Root.Seconds),6}  " +
                    $"{run.Trigger ?? "?",-9}  {run.Outcome,-10}  {(run.TabsClosed is { } closed ? closed.ToString(CultureInfo.InvariantCulture) : "-"),6}  " +
                    $"{TabPlanText.Clip(Grouped(run), 15),-15}  {Duration(run.WaitedSeconds),6}  " +
                    $"{(run.Checks != 0 ? run.Checks.ToString(CultureInfo.InvariantCulture) : "-"),6}  " +
                    $"{TabPlanText.Clip(run.LastBlock ?? string.Empty, 60)}")),
        ];
    }

    /// <summary>The run's spans as an indented tree, with durations, attributes and events.</summary>
    public static IReadOnlyList<string> RenderTree(TraceRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        List<string> lines = [];
        void Visit(TraceSpan span, int depth)
        {
            var indent = new string(' ', 2 * depth);
            var flag = span.Incomplete ? " [incomplete]" : span.Error.Length > 0 ? $" [error: {TabPlanText.Clip(span.Error, 80)}]" : string.Empty;
            var offset = (span.StartNs - run.Root.StartNs) / 1e9;
            lines.Add($"{indent}{span.Name}  {Duration(span.Seconds)} at +{NumberText.Fixed(offset, 1)}s{flag}");
            if (AttributeText(span.Attributes, HiddenAttributes, 60) is { Length: > 0 } attributes)
            {
                lines.Add($"{indent}    {attributes}");
            }

            foreach (var spanEvent in span.Events)
            {
                var at = (spanEvent.AtNs - run.Root.StartNs) / 1e9;
                var detail = AttributeText(spanEvent.Attributes, HiddenEventAttributes, 100);
                lines.Add($"{indent}    - +{NumberText.Fixed(at, 1)}s {spanEvent.Name} {detail}".TrimEnd());
            }

            foreach (var child in run.Children(span))
            {
                Visit(child, depth + 1);
            }
        }

        Visit(run.Root, 0);
        return lines;
    }

    /// <summary>Aggregates runs: outcomes, grouping verdicts, waits, closes and the commonest block reasons.</summary>
    public static TraceSummary Summarise(IReadOnlyList<TraceRun> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        var completed = found.Where(run => run.Outcome != "incomplete").ToList();
        List<double> waits = [.. found.Where(run => run.Checks != 0 && run.WaitedSeconds is not null).Select(run => run.WaitedSeconds!.Value)];

        // Reasons quote live numbers ("used 12s ago"); fold them so like counts with like.
        var reasons = Tally(found.SelectMany(run => run.Blocks).Select(block => Digits().Replace(block, "N")));
        return new TraceSummary(
            found.Count,
            completed.Count,
            completed.Count(run => run.Outcome is "ok" or "dry_run"),
            found.Count - completed.Count,
            Tally(found.Select(run => run.Grouping)),
            waits,
            [.. completed.Select(run => run.Root.Seconds)],
            found.Sum(run => run.TabsClosed ?? 0),
            [.. reasons.OrderByDescending(pair => pair.Value).Take(5).Select(pair => new BlockCount(pair.Key, pair.Value))]);
    }

    /// <summary>The summary as lines, headed by the window of <paramref name="days"/> it covers.</summary>
    public static IReadOnlyList<string> RenderSummary(TraceSummary summary, int days)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var rate = summary.SuccessRate is { } success ? $"{NumberText.Fixed(success * 100, 0)}%" : "-";
        var grouping = string.Join(", ", summary.Grouping.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value}"));
        List<string> lines =
        [
            $"Last {days} days: {summary.Runs} runs, {summary.Completed} completed, {summary.Incomplete} incomplete",
            $"  success rate        {rate} ({summary.Ok}/{summary.Completed})",
            $"  grouping            {(grouping.Length > 0 ? grouping : "-")}",
            $"  waited for idle     {summary.Waits.Count} runs, median {Duration(Quantile(summary.Waits, 0.5))}, " +
            $"p90 {Duration(Quantile(summary.Waits, 0.9))}",
            $"  run duration        median {Duration(Median(summary.Durations))}, p90 {Duration(Quantile(summary.Durations, 0.9))}",
            string.Create(CultureInfo.InvariantCulture, $"  tabs closed         {summary.Closed}"),
        ];
        if (summary.Blocks.Count > 0)
        {
            lines.Add("  commonest blocks");
            lines.AddRange(summary.Blocks.Select(block => $"    {block.Count,4}  {block.Reason}"));
        }

        return lines;
    }

    internal static string? Str(IReadOnlyDictionary<string, object?> attributes, string key) =>
        attributes.GetValueOrDefault(key) as string;

    internal static bool? Bool(IReadOnlyDictionary<string, object?> attributes, string key) =>
        attributes.GetValueOrDefault(key) is bool value ? value : null;

    internal static long? Int(IReadOnlyDictionary<string, object?> attributes, string key) =>
        attributes.GetValueOrDefault(key) is long value ? value : null;

    internal static double? Float(IReadOnlyDictionary<string, object?> attributes, string key) =>
        attributes.GetValueOrDefault(key) switch
        {
            double value => value,
            long value => value,
            _ => null,
        };

    // A seconds count: "250ms", "12.5s", "3.0m", or "-".
    private static string Duration(double? seconds) =>
        seconds switch
        {
            null => "-",
            < 1 => $"{NumberText.Fixed(seconds.Value * 1000, 0)}ms",
            < 120 => $"{NumberText.Fixed(seconds.Value, 1)}s",
            _ => $"{NumberText.Fixed(seconds.Value / 60, 1)}m",
        };

    private static string Grouped(TraceRun run) =>
        run.Grouping is "grouped" or "stopped" && run.GroupPlanned is { } planned
            ? $"{run.Grouping} {YamlString(run.GroupMoved)}/{planned.ToString(CultureInfo.InvariantCulture)}"
            : run.Grouping;

    private static string AttributeText(IReadOnlyDictionary<string, object?> attributes, HashSet<string> hidden, int width) =>
        string.Join(" ", attributes.Where(pair => !hidden.Contains(pair.Key)).Select(pair => $"{pair.Key}={TabPlanText.Clip(YamlString(pair.Value), width)}"));

    private static List<KeyValuePair<string, int>> Tally(IEnumerable<string> items)
    {
        var counts = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            counts[item] = counts.GetValueOrDefault(item) + 1;
        }

        return [.. counts];
    }

    private static double? Quantile(IReadOnlyList<double> values, double q)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.Order().ToList();
        return ordered[Math.Min(ordered.Count - 1, (int)Math.Round(q * (ordered.Count - 1), MidpointRounding.ToEven))];
    }

    private static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.Order().ToList();
        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2;
    }

    // A decoded attribute value as text.
    private static string YamlString(object? value) =>
        value switch
        {
            null => "None",
            string text => text,
            bool flag => flag ? "True" : "False",
            long whole => whole.ToString(CultureInfo.InvariantCulture),
            double real => YamlFloat.Repr(real),
            // The only other decoded value is an arrayValue's list.
            _ => "[" + string.Join(", ", ((IEnumerable<object?>)value).Select(item => item is string text ? QuotedText.Str(text) : YamlString(item))) + "]",
        };

    private static TraceSpan? Span(FileSpan file, Action<string>? skipped)
    {
        try
        {
            var span = JsonProblem.Read(file.Span.ToJsonString(), OtlpJsonContext.Default.ChromeSpan);
            return new TraceSpan(
                file.TraceId,
                file.SpanId,
                span.ParentSpanId,
                span.Name,
                span.StartTimeUnixNano,
                span.EndTimeUnixNano,
                span.Status.Code == OtlpStatusError ? Message(span.Status.Message) : string.Empty,
                OtlpAttributes.ByName(span.Attributes),
                [.. span.Events.Select(spanEvent => new TraceEvent(spanEvent.Name, spanEvent.TimeUnixNano, OtlpAttributes.ByName(spanEvent.Attributes)))]);
        }
        catch (JsonInputException e)
        {
            skipped?.Invoke(e.Under(file.Origin, file.SpanPath).Message);
            return null;
        }
    }

    // The message of an error status; absent or null reads as empty.
    private static string Message(JsonElement? message) =>
        message switch
        {
            null or { ValueKind: JsonValueKind.Null } => string.Empty,
            { ValueKind: JsonValueKind.String } text => text.GetString()!,
            { } other => throw JsonProblem.Expected("$.status.message", "a string", JsonProblem.Describe(other)),
        };

    [GeneratedRegex(@"^([0-9]{4}-[0-9]{2}-[0-9]{2})\.ndjson$")]
    private static partial Regex DayFile();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
