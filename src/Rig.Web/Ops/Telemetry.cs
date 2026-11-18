using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Core.Observability;
using Rig.Core.Otel;
using Rig.Core.Roots;

[assembly: SuppressMessage("Design", "CA1054", Justification = "URLs are kept as written for the SPA and the MCP answer; System.Uri normalizes them.", Scope = "namespaceanddescendants", Target = "~N:Rig.Web.Ops")]
[assembly: SuppressMessage("Design", "CA1056", Justification = "URLs are kept as written for the SPA and the MCP answer; System.Uri normalizes them.", Scope = "namespaceanddescendants", Target = "~N:Rig.Web.Ops")]

namespace Rig.Web.Ops;

/// <summary>What one trace list row needs.</summary>
/// <param name="Days">How far back, 1 to 30.</param>
/// <param name="Limit">How many traces, 1 to 500.</param>
/// <param name="Service">Only this service's traces; null for all.</param>
public sealed record TraceQuery(int Days = 1, int Limit = 100, string? Service = null);

/// <summary>One trace, as a list row shows it.</summary>
/// <param name="TraceId">The trace's id.</param>
/// <param name="Service">Its root service.</param>
/// <param name="Root">Its root span's name.</param>
/// <param name="StartNs">When it started, in Unix nanoseconds.</param>
/// <param name="DurationMs">How long it took.</param>
/// <param name="Spans">How many spans it holds.</param>
/// <param name="Errors">How many of them failed.</param>
public sealed record TraceView(string TraceId, string Service, string Root, long StartNs, double DurationMs, int Spans, int Errors);

/// <summary>Where the traces came from, and why not ClickHouse when it did not answer.</summary>
/// <param name="Source"><c>clickhouse</c> or <c>ndjson</c>.</param>
/// <param name="Note">Why ClickHouse did not answer; empty when it did.</param>
/// <param name="Traces">The traces, newest first.</param>
public sealed record TraceListView(string Source, string Note, IReadOnlyList<TraceView> Traces);

/// <summary>Which trace store answers.</summary>
/// <param name="Source"><c>clickhouse</c> or <c>ndjson</c>.</param>
/// <param name="ClickhouseReachable">Whether ClickHouse answered its last probe.</param>
/// <param name="Detail">What that means for the list.</param>
public sealed record TelemetryStore(string Source, bool ClickhouseReachable, string Detail);

/// <summary>The configured trace UIs and whether each answers.</summary>
/// <param name="HyperdxUrl">HyperDX's base URL; empty when not configured.</param>
/// <param name="HyperdxReachable">Whether it answers.</param>
/// <param name="HyperdxTraceUrl">Its trace search template, with <c>{trace_id}</c> left for the browser.</param>
/// <param name="AspireUrl">Aspire's base URL; empty when not configured.</param>
/// <param name="AspireReachable">Whether it answers.</param>
/// <param name="AspireTraceUrl">Its trace page template.</param>
public sealed record ExplorerStatusView(
    string HyperdxUrl,
    bool HyperdxReachable,
    string HyperdxTraceUrl,
    string AspireUrl,
    bool AspireReachable,
    string AspireTraceUrl);

/// <summary>Which trace store answers and which trace explorers are up.</summary>
/// <param name="Store">ClickHouse or the local NDJSON files.</param>
/// <param name="Explorers">HyperDX and Aspire.</param>
public sealed record TelemetryView(TelemetryStore Store, ExplorerStatusView Explorers);

/// <summary>
/// ClickHouse when it answers, the NDJSON files otherwise, plus the explorer UIs. Reachability is remembered for <see cref="RecheckSeconds"/>, so a stopped stack costs one short timeout per half
/// minute rather than one per request. A down ClickHouse is not an error; only a file read that throws is.
/// </summary>
public sealed class Telemetry : IDisposable
{
    /// <summary>How long a reachability answer is trusted.</summary>
    public const double RecheckSeconds = 30;

    /// <summary>How much of a ClickHouse failure the note quotes.</summary>
    public const int NoteLimit = 300;

    private readonly Lock _gate = new();
    private readonly Func<Task<bool>> _reachable;
    private readonly Func<TraceQuery, Task<IReadOnlyList<TraceView>>> _clickhouse;
    private readonly Func<TraceQuery, IReadOnlyList<TraceView>> _files;
    private readonly Func<CancellationToken, Task<ExplorerStatusView>> _explorers;
    private readonly Func<double> _clock;
    private readonly IDisposable? _owned;
    private (double CheckedAt, bool Reachable)? _state;

    /// <summary>Builds a source over the seams; every one is injectable for tests.</summary>
    public Telemetry(
        Func<Task<bool>> clickhouseReachable,
        Func<TraceQuery, Task<IReadOnlyList<TraceView>>> clickhouseRecent,
        Func<TraceQuery, IReadOnlyList<TraceView>> fileRecent,
        Func<CancellationToken, Task<ExplorerStatusView>> explorers,
        Func<double> clock,
        IDisposable? owned = null)
    {
        ArgumentNullException.ThrowIfNull(clickhouseReachable);
        ArgumentNullException.ThrowIfNull(clickhouseRecent);
        ArgumentNullException.ThrowIfNull(fileRecent);
        ArgumentNullException.ThrowIfNull(explorers);
        ArgumentNullException.ThrowIfNull(clock);
        _reachable = clickhouseReachable;
        _clickhouse = clickhouseRecent;
        _files = fileRecent;
        _explorers = explorers;
        _clock = clock;
        _owned = owned;
    }

    /// <summary>
    /// The real source: ClickHouse at <see cref="ClickHouseClient.LocalUrl"/> and the tracer's day files under
    /// <paramref name="checkout"/>.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000", Justification = "The HttpClients this source creates own their handlers and this source disposes both.")]
    public static Telemetry ForCheckout(string checkout) => ForTraces(Path.Join(checkout, "temp", "traces", "sdk"));

    /// <summary>
    /// The real source with this checkout's traced folder given directly: ClickHouse at
    /// <see cref="ClickHouseClient.LocalUrl"/> and rig's day files, including <paramref name="checkoutTraces"/>.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000", Justification = "The HttpClients this source creates own their handlers and this source disposes both.")]
    public static Telemetry ForTraces(string checkoutTraces)
    {
        ArgumentException.ThrowIfNullOrEmpty(checkoutTraces);
        var clickhouseHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var probeHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(0.5) };
        var clickhouse = new ClickHouseClient(clickhouseHttp);
        var roots = Backfill.DefaultRoots(
            AppData.Current(),
            checkoutTraces,
            Environment.GetEnvironmentVariable(Backfill.TraceDirEnv));
        var explorers = new Explorers(probeHttp, TimeProvider.System);
        return new Telemetry(
            async () => await clickhouse.PingAsync().ConfigureAwait(false),
            query => ClickHouse(clickhouse, query),
            query => Files(roots, query),
            explorers.StatusAsync,
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            new Combined(clickhouseHttp, probeHttp));
    }

    /// <summary>Whether ClickHouse answered its last probe, probing again once <see cref="RecheckSeconds"/> has passed.</summary>
    public async Task<bool> ClickhouseReachableAsync()
    {
        double now;
        lock (_gate)
        {
            now = _clock();
            if (_state is { } state && now - state.CheckedAt < RecheckSeconds)
            {
                return state.Reachable;
            }
        }

        var reachable = await _reachable().ConfigureAwait(false);
        lock (_gate)
        {
            _state = (now, reachable);
        }

        return reachable;
    }

    /// <summary>Which store answers and which explorers are up.</summary>
    /// <param name="cancellationToken">Cancels the explorer probes.</param>
    public async Task<TelemetryView> StatusAsync(CancellationToken cancellationToken = default)
    {
        var store = await ClickhouseReachableAsync().ConfigureAwait(false)
            ? new TelemetryStore("clickhouse", true, "reading ClickHouse's otel tables")
            : new TelemetryStore("ndjson", false, "ClickHouse unreachable: reading local NDJSON trace files");
        return new TelemetryView(store, await _explorers(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Recent traces, newest first, from ClickHouse when it answers and the files otherwise.</summary>
    public async Task<TraceListView> RecentAsync(TraceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var note = "ClickHouse unreachable: listing traces from local NDJSON files";
        if (await ClickhouseReachableAsync().ConfigureAwait(false))
        {
            try
            {
                var found = await _clickhouse(query).ConfigureAwait(false);
                return new TraceListView("clickhouse", string.Empty, found);
            }
            catch (Exception e) when (e is ClickHouseException or JsonException)
            {
                lock (_gate)
                {
                    _state = (_clock(), false);
                }

                var text = e.Message.Length > NoteLimit ? e.Message[..NoteLimit] : e.Message;
                note = $"ClickHouse query failed, listing traces from local NDJSON files: {text}";
            }
        }

        return new TraceListView("ndjson", note, _files(query));
    }

    /// <inheritdoc/>
    public void Dispose() => _owned?.Dispose();

    internal static async Task<IReadOnlyList<TraceView>> ClickHouse(ClickHouseClient clickhouse, TraceQuery query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["days"] = query.Days.ToString(CultureInfo.InvariantCulture),
            ["limit"] = query.Limit.ToString(CultureInfo.InvariantCulture),
            ["service"] = query.Service ?? string.Empty,
        };
        var rows = await clickhouse.RowsAsync(Sql, parameters, TraceRowContext.Default.TraceRow).ConfigureAwait(false);
        return [.. rows.Select(row => new TraceView(row.TraceId, row.Service, row.Root, row.StartNs, Math.Max(row.EndNs - row.StartNs, 0) / 1_000_000.0, row.Spans, row.Errors))];
    }

    internal static IReadOnlyList<TraceView> Files(IReadOnlyList<string> roots, TraceQuery query)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-query.Days);
        var cutoffNs = (cutoff.UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
        var spans = new Dictionary<(string TraceId, string SpanId), TraceSpanInfo>();
        foreach (var root in roots)
        {
            foreach (var span in TraceAnalytics.Load(root, cutoff).Spans)
            {
                var key = (span.TraceId, span.SpanId);
                if (!spans.TryGetValue(key, out var held) || (held.Incomplete && !span.Incomplete))
                {
                    spans[key] = span;
                }
            }
        }

        return
        [
            .. spans.Values
                .GroupBy(span => span.TraceId, StringComparer.Ordinal)
                .Select(group => Summarize(group.ToList()))
                .Where(summary => summary.StartNs >= cutoffNs && (string.IsNullOrEmpty(query.Service) || summary.Service == query.Service))
                .OrderByDescending(summary => summary.StartNs)
                .Take(query.Limit),
        ];
    }

    internal static TraceView Summarize(List<TraceSpanInfo> spans)
    {
        var roots = spans.Where(span => span.ParentSpanId.Length == 0).ToList();
        var first = (roots.Count > 0 ? roots : [.. spans]).MinBy(span => span.StartNs)!;
        var start = spans.Min(span => span.StartNs);
        var end = spans.Max(span => span.EndNs);
        return new TraceView(
            spans[0].TraceId,
            first.Tool,
            first.Name,
            start,
            Math.Max(end - start, 0) / 1_000_000.0,
            spans.Count,
            spans.Count(span => span.StatusCode == 2 || (span.ExitCode is { } ec && ec != 0)));
    }

    private const string Sql = """
        SELECT
            TraceId AS trace_id,
            if(countIf(ParentSpanId = '') > 0, argMinIf(ServiceName, Timestamp, ParentSpanId = ''),
               argMin(ServiceName, Timestamp)) AS service,
            if(countIf(ParentSpanId = '') > 0, argMinIf(SpanName, Timestamp, ParentSpanId = ''),
               argMin(SpanName, Timestamp)) AS root,
            toInt64(toUnixTimestamp64Nano(min(Timestamp))) AS start_ns,
            toInt64(max(toUnixTimestamp64Nano(Timestamp) + toInt64(Duration))) AS end_ns,
            toInt64(uniqExact(SpanId)) AS spans,
            toInt64(uniqExactIf(SpanId, StatusCode IN ('Error', 'STATUS_CODE_ERROR'))) AS errors
        FROM otel.otel_traces
        WHERE Timestamp >= now() - INTERVAL {days:UInt32} DAY
        GROUP BY TraceId
        HAVING {service:String} = '' OR service = {service:String}
        ORDER BY start_ns DESC
        LIMIT {limit:UInt32}
        """;

    private sealed class Combined(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }

    /// <summary>The configured explorers and whether each answers; one instance per source, so its cache is its own.</summary>
    /// <param name="probe">The client that asks each UI.</param>
    /// <param name="time">The clock the recheck window is measured against.</param>
    internal sealed class Explorers(HttpClient probe, TimeProvider time)
    {
        public const string HyperdxVariable = "RIG_HYPERDX_URL";
        public const string AspireVariable = "RIG_ASPIRE_URL";
        public const string DefaultAspire = "http://localhost:18888";
        public const string HyperdxTracePath = "/search?where=TraceId%3D%27{trace_id}%27&whereLanguage=sql";
        public const string AspireTracePath = "/traces/detail/{trace_id}";

        private readonly Lock _gate = new();
        private readonly Dictionary<string, (double CheckedAt, bool Reachable)> _seen = new(StringComparer.Ordinal);
        private double _checkedAt;

        /// <summary>The configured explorers, each probed at most once per <see cref="RecheckSeconds"/>.</summary>
        /// <param name="cancellationToken">Cancels a probe; a cancelled probe is not remembered.</param>
        public async Task<ExplorerStatusView> StatusAsync(CancellationToken cancellationToken = default)
        {
            var hyperdx = (Environment.GetEnvironmentVariable(HyperdxVariable) ?? string.Empty).Trim().TrimEnd('/');
            var aspire = (Environment.GetEnvironmentVariable(AspireVariable) ?? DefaultAspire).Trim().TrimEnd('/');
            var now = Now();
            lock (_gate)
            {
                if (now - _checkedAt >= RecheckSeconds)
                {
                    _seen.Clear();
                    _checkedAt = now;
                }
            }

            return new ExplorerStatusView(
                hyperdx,
                await ReachableAsync(hyperdx, now, cancellationToken).ConfigureAwait(false),
                hyperdx.Length > 0 ? hyperdx + HyperdxTracePath : string.Empty,
                aspire,
                await ReachableAsync(aspire, now, cancellationToken).ConfigureAwait(false),
                aspire.Length > 0 ? aspire + AspireTracePath : string.Empty);
        }

        /// <summary>Forgets every remembered answer, so the next status probes again.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _seen.Clear();
                _checkedAt = 0;
            }
        }

        private double Now() => time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

        private async Task<bool> ReachableAsync(string url, double now, CancellationToken cancellationToken)
        {
            if (url.Length == 0)
            {
                return false;
            }

            lock (_gate)
            {
                if (_seen.TryGetValue(url, out var cached) && now - cached.CheckedAt < RecheckSeconds)
                {
                    return cached.Reachable;
                }
            }

            var reachable = await ProbeAsync(url, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _seen[url] = (now, reachable);
            }

            return reachable;
        }

        // Any status counts: a login redirect is still a live UI.
        private async Task<bool> ProbeAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await probe
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested
                && e is (HttpRequestException or TaskCanceledException or UriFormatException))
            {
                return false;
            }
        }
    }
}

/// <summary>The compile-time metadata for the trace list's JSONEachRow rows: snake_case names, every member required.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Strict,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    AllowDuplicateProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TraceRow))]
internal sealed partial class TraceRowContext : JsonSerializerContext;

internal sealed record TraceRow(string TraceId, string Service, string Root, long StartNs, long EndNs, int Spans, int Errors);
