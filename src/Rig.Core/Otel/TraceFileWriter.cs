using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Rig.Core.Otel;

/// <summary>
/// Writes the spans of the listened sources as OTLP-JSON day files, one envelope per line: the contract of
/// <c>rig.tracing</c> and workspace's <c>common.tracing</c>, so the same readers and the collector's file replay take
/// either language's output. Tracing never breaks the tool: a write that fails drops what was buffered.
/// </summary>
/// <remarks>
/// <para><c>SHARED_UTILITIES_TRACE_DIR</c> overrides every path (tests stay isolated);
/// <c>SHARED_UTILITIES_TRACE_HOME</c> replaces only the default root; <c>SHARED_UTILITIES_TRACING=0</c> (or
/// false, no, off) writes nothing.</para>
/// </remarks>
public sealed class TraceFileWriter : IDisposable
{
    /// <summary>Overrides every trace path.</summary>
    public const string DirVariable = "SHARED_UTILITIES_TRACE_DIR";

    /// <summary>Replaces the default root only.</summary>
    public const string HomeVariable = "SHARED_UTILITIES_TRACE_HOME";

    /// <summary>Off when set to 0, false, no or off.</summary>
    public const string EnabledVariable = "SHARED_UTILITIES_TRACING";

    /// <summary>Buffered spans that force a write.</summary>
    public const int FlushAt = 2000;

    private static readonly string[] Off = ["0", "false", "no", "off"];
    private static readonly DateTime Epoch = DateTime.UnixEpoch;

    private readonly string _tool;
    private readonly IReadOnlyDictionary<string, string> _scopes;
    private readonly Func<string, string?> _environment;
    private readonly string _defaultRoot;
    private readonly TimeProvider _time;
    private readonly ActivityListener _listener;
    private readonly ConcurrentDictionary<Activity, byte> _open = new();
    private readonly List<(string Scope, Dictionary<string, object?> Span)> _buffer = [];
    private readonly Lock _lock = new();
    private Timer? _flusher;
    private string? _directory;

    /// <summary>Starts listening to <paramref name="scopes"/>' sources, each written under its scope name.</summary>
    /// <param name="tool">The <c>service.name</c>, and the default folder's name.</param>
    /// <param name="scopes">Source name to the scope name its spans are written under.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="defaultRoot">The root a tool writes beneath when no variable says otherwise.</param>
    /// <param name="time">The clock that names the day file and ends incomplete spans.</param>
    public TraceFileWriter(string tool, IReadOnlyDictionary<string, string> scopes, Func<string, string?> environment, string defaultRoot, TimeProvider time)
    {
        _tool = tool;
        _scopes = scopes;
        _environment = environment;
        _defaultRoot = defaultRoot;
        _time = time;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => _scopes.ContainsKey(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _open[activity] = 0,
            ActivityStopped = Stopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Whether the environment leaves tracing on.</summary>
    public bool Enabled => !Off.Contains(_environment(EnabledVariable)?.Trim().ToLowerInvariant() ?? "1", StringComparer.Ordinal);

    /// <summary>The file the next write appends to.</summary>
    public string TraceFile
    {
        get
        {
            var day = _time.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".ndjson";
            var overrideDir = _environment(DirVariable);
            lock (_lock)
            {
                if (_directory is not null && string.IsNullOrEmpty(overrideDir))
                {
                    return Path.Join(_directory, day);
                }
            }

            var root = !string.IsNullOrEmpty(overrideDir) ? overrideDir : _environment(HomeVariable);
            return Path.Join(string.IsNullOrEmpty(root) ? _defaultRoot : root, _tool, day);
        }
    }

    /// <summary>Writes <c>&lt;directory&gt;/&lt;YYYY-MM-DD&gt;.ndjson</c> from the next write on; null restores the default.</summary>
    public void SetDirectory(string? directory)
    {
        lock (_lock)
        {
            WriteLocked();
            _directory = directory;
        }
    }

    /// <summary>Writes what is buffered; <paramref name="includeOpen"/> also writes every running span as it stands.</summary>
    public void Flush(bool includeOpen = false)
    {
        lock (_lock)
        {
            if (includeOpen && Enabled)
            {
                foreach (var activity in _open.Keys)
                {
                    _buffer.Add((ScopeOf(activity), Record(activity, incomplete: true)));
                }
            }

            WriteLocked();
        }
    }

    /// <summary>Writes every <paramref name="interval"/> until disposed; a second call keeps the first timer.</summary>
    public void FlushPeriodically(TimeSpan interval)
    {
        lock (_lock)
        {
            _flusher ??= new Timer(_ => Flush(), null, interval, interval);
        }
    }

    /// <summary>Stops listening and the periodic write, then writes what is buffered.</summary>
    public void Dispose()
    {
        _listener.Dispose();
        lock (_lock)
        {
            _flusher?.Dispose();
            _flusher = null;
            WriteLocked();
        }
    }

    private void Stopped(Activity activity)
    {
        _open.TryRemove(activity, out _);
        if (!Enabled)
        {
            return;
        }

        var record = Record(activity, incomplete: false);
        lock (_lock)
        {
            _buffer.Add((ScopeOf(activity), record));
            if (_buffer.Count >= FlushAt)
            {
                WriteLocked();
            }
        }
    }

    private string ScopeOf(Activity activity) => _scopes.GetValueOrDefault(activity.Source.Name, activity.Source.Name);

    private Dictionary<string, object?> Record(Activity activity, bool incomplete)
    {
        var start = Nanos(activity.StartTimeUtc);
        var end = incomplete ? Nanos(_time.GetUtcNow().UtcDateTime) : start + (activity.Duration.Ticks * 100);
        Dictionary<string, object?> status = new() { ["code"] = activity.Status == ActivityStatusCode.Error ? 2 : 1 };
        if (activity.Status == ActivityStatusCode.Error)
        {
            status["message"] = activity.StatusDescription ?? string.Empty;
        }

        Dictionary<string, object?> record = new()
        {
            ["traceId"] = activity.TraceId.ToHexString(),
            ["spanId"] = activity.SpanId.ToHexString(),
            ["parentSpanId"] = activity.ParentSpanId == default ? string.Empty : activity.ParentSpanId.ToHexString(),
            ["name"] = activity.OperationName,
            ["startTimeUnixNano"] = start.ToString(CultureInfo.InvariantCulture),
            ["endTimeUnixNano"] = end.ToString(CultureInfo.InvariantCulture),
            ["status"] = status,
        };
        List<KeyValuePair<string, object?>> tags = [.. activity.TagObjects];
        if (incomplete)
        {
            tags.Add(new("span.incomplete", true));
        }

        if (tags.Count > 0)
        {
            record["attributes"] = Attributes(tags);
        }

        List<object?> events = [];
        foreach (var happened in activity.Events)
        {
            Dictionary<string, object?> recorded = new()
            {
                ["name"] = happened.Name,
                ["timeUnixNano"] = Nanos(happened.Timestamp.UtcDateTime).ToString(CultureInfo.InvariantCulture),
            };
            List<KeyValuePair<string, object?>> attributes = [.. happened.Tags];
            if (attributes.Count > 0)
            {
                recorded["attributes"] = Attributes(attributes);
            }

            events.Add(recorded);
        }

        if (events.Count > 0)
        {
            record["events"] = events;
        }

        return record;
    }

    private static long Nanos(DateTime utc) => (utc - Epoch).Ticks * 100;

    private static List<object?> Attributes(IEnumerable<KeyValuePair<string, object?>> tags) =>
        [.. tags.Select(tag => new Dictionary<string, object?> { ["key"] = tag.Key, ["value"] = AttributeValue(tag.Value) })];

    /// <summary>One attribute value in OTLP-JSON, the encoding <c>rig.tracing</c> gives the same Python value.</summary>
    public static Dictionary<string, object?> AttributeValue(object? value) => value switch
    {
        bool flag => new() { ["boolValue"] = flag },
        sbyte or byte or short or ushort or int or uint or long or ulong =>
            new() { ["intValue"] = Convert.ToString(value, CultureInfo.InvariantCulture) },
        float or double or decimal => new() { ["doubleValue"] = Convert.ToDouble(value, CultureInfo.InvariantCulture) },
        string text => new() { ["stringValue"] = text },
        IEnumerable items => new() { ["arrayValue"] = new Dictionary<string, object?> { ["values"] = items.Cast<object?>().Select(AttributeValue).ToList() } },
        _ => new() { ["stringValue"] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty },
    };

    private void WriteLocked()
    {
        if (_buffer.Count == 0 || !Enabled)
        {
            _buffer.Clear();
            return;
        }

        List<object?> scopeSpans = [.. _buffer
            .GroupBy(item => item.Scope, StringComparer.Ordinal)
            .Select(group => new Dictionary<string, object?>
            {
                ["scope"] = new Dictionary<string, object?> { ["name"] = group.Key },
                ["spans"] = group.Select(item => item.Span).ToList(),
            })];
        _buffer.Clear();
        Dictionary<string, object?> envelope = new()
        {
            ["resourceSpans"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["resource"] = new Dictionary<string, object?> { ["attributes"] = Attributes([new("service.name", _tool)]) },
                    ["scopeSpans"] = scopeSpans,
                },
            },
        };
        try
        {
            var path = TraceFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(envelope) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // tracing must never break the tool
        }
    }
}
