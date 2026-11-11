using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Otel;

/// <summary>A clock whose time moves only when told: <see cref="SleepAsync"/> advances it instead of waiting.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private long ticks;

    public DateTimeOffset Now { get; private set; } = now;

    public List<TimeSpan> Slept { get; } = [];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => Now;

    public override long GetTimestamp() => ticks;

    public void Advance(TimeSpan by)
    {
        ticks += by.Ticks;
        Now += by;
    }

    public Task SleepAsync(TimeSpan by, CancellationToken cancellationToken)
    {
        Slept.Add(by);
        Advance(by);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A ClickHouse HTTP server in memory: answers <c>SELECT 1</c>, the table check, table stats and the span lookup from
/// what it holds, and records every statement.
/// </summary>
internal sealed class FakeClickHouse : HttpMessageHandler
{
    public HashSet<SpanKey> Spans { get; } = [];

    public bool HasTraces { get; set; } = true;

    public string Stats { get; set; } = string.Empty;

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string? Error { get; set; }

    /// <summary>Thrown instead of answering, as a connection failure or timeout would be.</summary>
    public Exception? Throws { get; set; }

    /// <summary>Answers only the span lookup with this error.</summary>
    public string? LookupError { get; set; }

    public List<(string Sql, Dictionary<string, string> Query, string? Auth)> Requests { get; } = [];

    public List<Uri> Uris { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A real handler's cancellation, which the client must pass on rather than call unreachable.
        if (cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException(null, null, cancellationToken);
        }

        if (Throws is not null)
        {
            throw Throws;
        }

        var sql = await request.Content!.ReadAsStringAsync(cancellationToken);
        var query = request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
        Requests.Add((sql, query, request.Headers.Authorization?.ToString()));
        Uris.Add(request.RequestUri);
        if (LookupError is not null && query.ContainsKey("param_ids"))
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(LookupError) };
        }

        if (Error is not null || Status != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(Status == HttpStatusCode.OK ? HttpStatusCode.InternalServerError : Status) { Content = new StringContent(Error ?? string.Empty) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer(sql, query)) };
    }

    private string Answer(string sql, Dictionary<string, string> query)
    {
        if (sql == "SELECT 1")
        {
            return "1\n";
        }

        if (sql.StartsWith("SELECT count() FROM system.tables", StringComparison.Ordinal))
        {
            return HasTraces && query["param_table"] == "otel_traces" ? "1\n" : "0\n";
        }

        if (sql.Contains("bytes_on_disk", StringComparison.Ordinal))
        {
            return Stats;
        }

        if (!query.TryGetValue("param_ids", out var lookup))
        {
            return "ok\n";
        }

        var ids = lookup.Trim('[', ']').Split(", ").Select(id => id.Trim('\'')).ToHashSet(StringComparer.Ordinal);
        return string.Concat(Spans.Where(key => ids.Contains(key.TraceId)).Select(key => $$"""{"TraceId":"{{key.TraceId}}","SpanId":"{{key.SpanId}}"}""" + "\n"));
    }
}

/// <summary>An HTTP handler that answers every request with <paramref name="send"/>.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}

/// <summary>An OTLP sender that records each body; trace spans it accepts land in <see cref="Lands"/> when set.</summary>
internal sealed class FakeSender : IOtlpSender
{
    public List<(string Signal, string Body)> Sent { get; } = [];

    public FakeClickHouse? Lands { get; set; }

    /// <summary>Rejects the send with this index (zero-based), and every later one.</summary>
    public int RejectFrom { get; set; } = int.MaxValue;

    /// <summary>Cancels the send with this index (zero-based), as a cancelled token would mid-run.</summary>
    public int CancelAt { get; set; } = int.MaxValue;

    public Task<string?> SendAsync(string signal, string body, CancellationToken cancellationToken = default)
    {
        if (Sent.Count == CancelAt)
        {
            throw new OperationCanceledException();
        }

        if (Sent.Count >= RejectFrom)
        {
            return Task.FromResult<string?>("collector rejected a batch: HTTP 400 no");
        }

        Sent.Add((signal, body));
        if (Lands is not null && signal == "traces")
        {
            foreach (var resource in JsonNode.Parse(body)!["resourceSpans"]!.AsArray())
            {
                foreach (var scope in resource!["scopeSpans"]!.AsArray())
                {
                    foreach (var span in scope!["spans"]!.AsArray())
                    {
                        _ = Lands.Spans.Add(new SpanKey((string)span!["traceId"]!, (string)span["spanId"]!));
                    }
                }
            }
        }

        return Task.FromResult<string?>(null);
    }
}

/// <summary>Pids that are alive until signalled; a pid in <see cref="Stubborn"/> ignores SIGTERM.</summary>
internal sealed class FakeSignals : IProcessSignals
{
    public HashSet<int> Living { get; } = [];

    public HashSet<int> Stubborn { get; } = [];

    public List<(int Pid, bool Kill)> Sent { get; } = [];

    public bool Alive(int pid) => Living.Contains(pid);

    public void Signal(int pid, bool kill = false)
    {
        Sent.Add((pid, kill));
        if (kill || !Stubborn.Contains(pid))
        {
            _ = Living.Remove(pid);
        }
    }
}

/// <summary>Starts nothing: hands out pids from 100 up, which the fake signals then count alive, and records each start.</summary>
/// <summary>Records each start; with <paramref name="proc"/>, <c>ps</c> then shows the started process's command line.</summary>
internal sealed class RecordingDetached(FakeSignals signals, FakeProc? proc = null) : IDetachedStart
{
    private int next = 100;

    public List<(IReadOnlyList<string> Argv, string Log, string? WorkingDirectory, IReadOnlyDictionary<string, string>? Environment)> Starts { get; } = [];

    /// <summary>Whether started processes die at once.</summary>
    public bool DieAtOnce { get; set; }

    public Task<int> StartAsync(IReadOnlyList<string> argv, string log, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
    {
        Starts.Add((argv, log, workingDirectory, environment));
        var pid = next++;
        if (!DieAtOnce)
        {
            _ = signals.Living.Add(pid);
            _ = proc?.Answer(new Ran(0, string.Join(' ', argv) + "\n", string.Empty), "ps", "-o", "command=", "-p", pid.ToString(CultureInfo.InvariantCulture));
        }

        return Task.FromResult(pid);
    }
}

/// <summary>A port probe answering from a fixed set of listening ports.</summary>
internal sealed class FakeProbe : IPortProbe
{
    public HashSet<int> Listening { get; } = [];

    public Task<bool> ListeningAsync(int port, string host = CollectorConfig.Host, CancellationToken cancellationToken = default) =>
        Task.FromResult(Listening.Contains(port));
}

/// <summary>Trace file lines in the OTLP/JSON envelope rig's tracer writes.</summary>
internal static class TraceLines
{
    public static string Span(string trace, string span, long endNs, bool incomplete = false) =>
        $$$"""{"resourceSpans": [{"resource": {"attributes": []}, "scopeSpans": [{"scope": {"name": "rig"}, "spans": [{"traceId": "{{{trace}}}", "spanId": "{{{span}}}", "endTimeUnixNano": "{{{endNs}}}", "attributes": [{"key": "span.incomplete", "value": {"boolValue": {{{(incomplete ? "true" : "false")}}}}}]}]}]}]}""" + "\n";

    public static long Ns(DateTimeOffset at) => (at.UtcTicks - DateTime.UnixEpoch.Ticks) * 100;

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
