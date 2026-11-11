using System.Globalization;
using System.Text;

namespace Rig.Core.Otel;

/// <summary>
/// The always-on collector's config: OTLP in on loopback, one rotating JSON-lines file per signal out. Ported from
/// <c>rig.otel_stack.collector_config</c>, text for text.
/// </summary>
/// <remarks>
/// <para>Everything is chosen for an idle laptop on battery: receivers bind 127.0.0.1 only; <c>batch</c> (10 s) is the only
/// processor, and its timer runs only while data waits; <c>service.telemetry.metrics.level: none</c> turns off the
/// collector's own metrics endpoint; logs are <c>warn</c>; no <c>memory_limiter</c>, whose check interval would wake the
/// process forever.</para>
/// <para>Rotation only prunes when a file rotates, so <c>collector ensure</c> also deletes rotated files older than 7 days
/// (<see cref="Retention"/>).</para>
/// <para>Paths are written with forward slashes inside single quotes, any apostrophe doubled, so <c>/Users/O'Brien/...</c>
/// stays valid. With a forward endpoint set, every pipeline also sends there through its own small queue that gives up
/// after 30 s, so a downstream that is down never holds up the files.</para>
/// </remarks>
public static class CollectorConfig
{
    /// <summary>The loopback address the receivers bind.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>OTLP/gRPC.</summary>
    public const int GrpcPort = 4317;

    /// <summary>OTLP/HTTP.</summary>
    public const int HttpPort = 4318;

    /// <summary>The forward exporter's name.</summary>
    public const string ForwardExporter = "otlp_http/forward";

    private const int MaxMegabytes = 100;
    private const int MaxDays = 7;
    private const int MaxBackups = 50;
    private const int ForwardQueueSize = 200;
    private const string ForwardGiveUp = "30s";

    /// <summary><paramref name="text"/> as a YAML single-quoted scalar.</summary>
    public static string YamlQuoted(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>The config writing into <paramref name="data"/>, also forwarding to <paramref name="forward"/> when set.</summary>
    public static string Render(string data, string? forward = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var exporters = new StringBuilder();
        foreach (var signal in OtelPaths.Signals)
        {
            exporters.Append(CultureInfo.InvariantCulture, $"""
                  file/{signal}:
                    path: {YamlQuoted(Posix(Path.Join(data, $"{signal}.jsonl")))}
                    rotation:
                      max_megabytes: {MaxMegabytes}
                      max_days: {MaxDays}
                      max_backups: {MaxBackups}

                """);
        }

        if (forward is not null)
        {
            exporters.Append(CultureInfo.InvariantCulture, $"""
                  {ForwardExporter}:
                    endpoint: {YamlQuoted(forward)}
                    timeout: 2s
                    retry_on_failure:
                      enabled: true
                      initial_interval: 1s
                      max_interval: 5s
                      max_elapsed_time: {ForwardGiveUp}
                    sending_queue:
                      enabled: true
                      num_consumers: 1
                      queue_size: {ForwardQueueSize}

                """);
        }

        var extra = forward is not null ? $", {ForwardExporter}" : string.Empty;
        var pipelines = new StringBuilder();
        foreach (var signal in OtelPaths.Signals)
        {
            pipelines.Append(CultureInfo.InvariantCulture, $"""
                    {signal}:
                      receivers: [otlp]
                      processors: [batch]
                      exporters: [file/{signal}{extra}]

                """);
        }

        var text = $"""
            # Written by `otel-stack collector config`; regenerated on every start. See docs/otel-stack.md.
            receivers:
              otlp:
                protocols:
                  grpc:
                    endpoint: {Host}:{GrpcPort}
                  http:
                    endpoint: {Host}:{HttpPort}
            processors:
              batch:
                timeout: 10s
            exporters:
            {exporters}service:
              telemetry:
                metrics:
                  level: none
                logs:
                  level: warn
              pipelines:
            {pipelines}
            """;

        // A raw string takes the source file's line endings, CRLF on a Windows checkout; the config is always LF.
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>A path with forward slashes, as Python's <c>as_posix</c> writes it (backslashes are separators only on Windows).</summary>
    internal static string Posix(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
}
