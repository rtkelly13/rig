using System.Globalization;

namespace Rig.Core.Otel;

/// <summary>
/// Config for the on-demand pair: a laptop-sized ClickHouse, and the contrib collector feeding it. Ported from
/// <c>rig.otel_stack.clickhouse_config</c>, text for text.
/// </summary>
/// <remarks>
/// <para>ClickHouse binds 127.0.0.1 only, caps server memory at 1 GiB, shrinks the caches that default to gigabytes, runs a
/// 2-thread background pool, refreshes asynchronous metrics every 60 s instead of every second, and has no system log
/// tables at all. They are left <em>out</em> of the config rather than marked <c>remove="1"</c>: in a standalone config
/// <c>&lt;metric_log remove="1"/&gt;</c> stays in as an empty section, which turns the log on with its defaults. It logs to
/// the console at warning, which <c>clickhouse up</c> sends to one log file.</para>
/// <para>The contrib collector receives OTLP/HTTP on loopback <see cref="CollectorPort"/> (backfill posts there) and writes
/// through the <c>clickhouse</c> exporter, which creates the <c>otel</c> tables with a 7-day TTL. It exists only between
/// <c>up</c> and <c>down</c>.</para>
/// </remarks>
public static class ClickHouseConfig
{
    /// <summary>The loopback address everything binds.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>ClickHouse's HTTP interface.</summary>
    public const int HttpPort = 18123;

    /// <summary>ClickHouse's native TCP interface.</summary>
    public const int TcpPort = 19000;

    /// <summary>The contrib collector's OTLP/HTTP port, which backfill posts to.</summary>
    public const int CollectorPort = 14318;

    /// <summary>The database the exporter creates its tables in.</summary>
    public const string Database = "otel";

    /// <summary>How long the tables keep rows.</summary>
    public const int TtlHours = 7 * 24;

    /// <summary>ClickHouse's server memory cap, in bytes.</summary>
    public const long MaxServerMemory = 1L << 30;

    /// <summary>The server config, keeping its data in <paramref name="data"/> and its users in <paramref name="users"/>.</summary>
    public static string ServerXml(string data, string users)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(users);
        return Lf(string.Create(CultureInfo.InvariantCulture, $"""
        <?xml version="1.0"?>
        <!-- Written by `otel-stack clickhouse up`; regenerated on every start. -->
        <clickhouse>
            <logger>
                <level>warning</level>
                <console>1</console>
            </logger>
            <listen_host>{Host}</listen_host>
            <http_port>{HttpPort}</http_port>
            <tcp_port>{TcpPort}</tcp_port>
            <path>{Dir(data)}</path>
            <tmp_path>{Dir(Path.Join(data, "tmp"))}</tmp_path>
            <user_files_path>{Dir(Path.Join(data, "user_files"))}</user_files_path>
            <format_schema_path>{Dir(Path.Join(data, "format_schemas"))}</format_schema_path>
            <user_directories>
                <users_xml><path>{Escape(CollectorConfig.Posix(users))}</path></users_xml>
            </user_directories>

            <max_server_memory_usage>{MaxServerMemory}</max_server_memory_usage>
            <mark_cache_size>67108864</mark_cache_size>
            <index_mark_cache_size>33554432</index_mark_cache_size>
            <uncompressed_cache_size>16777216</uncompressed_cache_size>
            <index_uncompressed_cache_size>16777216</index_uncompressed_cache_size>
            <compiled_expression_cache_size>16777216</compiled_expression_cache_size>
            <background_pool_size>2</background_pool_size>
            <background_schedule_pool_size>4</background_schedule_pool_size>
            <max_concurrent_queries>16</max_concurrent_queries>
            <asynchronous_metrics_update_period_s>60</asynchronous_metrics_update_period_s>
            <asynchronous_heavy_metrics_update_period_s>600</asynchronous_heavy_metrics_update_period_s>
            <merge_tree>
                <number_of_free_entries_in_pool_to_execute_mutation>2</number_of_free_entries_in_pool_to_execute_mutation>
                <number_of_free_entries_in_pool_to_lower_max_size_of_merge>2</number_of_free_entries_in_pool_to_lower_max_size_of_merge>
                <number_of_free_entries_in_pool_to_execute_optimize_entire_partition>2</number_of_free_entries_in_pool_to_execute_optimize_entire_partition>
            </merge_tree>

            <!-- Idle wake-ups, each measured: a sampling profiler, a 2 s config poll, and pools sized for a server. -->
            <global_profiler_real_time_period_ns>0</global_profiler_real_time_period_ns>
            <global_profiler_cpu_time_period_ns>0</global_profiler_cpu_time_period_ns>
            <config_reload_interval_ms>3600000</config_reload_interval_ms>
            <async_insert_threads>1</async_insert_threads>
            <background_buffer_flush_schedule_pool_size>1</background_buffer_flush_schedule_pool_size>
            <background_distributed_schedule_pool_size>1</background_distributed_schedule_pool_size>
            <background_message_broker_schedule_pool_size>1</background_message_broker_schedule_pool_size>
            <background_fetches_pool_size>1</background_fetches_pool_size>
            <background_move_pool_size>1</background_move_pool_size>
            <background_common_pool_size>2</background_common_pool_size>
        </clickhouse>

        """));
    }

    /// <summary>One passwordless <c>default</c> user, reachable from loopback only, with a small per-query memory cap.</summary>
    public static string UsersXml() => Lf("""
        <?xml version="1.0"?>
        <clickhouse>
            <profiles>
                <default>
                    <max_threads>2</max_threads>
                    <max_memory_usage>536870912</max_memory_usage>
                </default>
            </profiles>
            <users>
                <default>
                    <password></password>
                    <networks>
                        <ip>127.0.0.1</ip>
                        <ip>::1</ip>
                    </networks>
                    <profile>default</profile>
                    <quota>default</quota>
                </default>
            </users>
            <quotas>
                <default></default>
            </quotas>
        </clickhouse>

        """);

    /// <summary>The contrib collector: OTLP/HTTP in on loopback, the clickhouse exporter out, 7-day TTL.</summary>
    public static string CollectorYaml() => Lf(string.Create(CultureInfo.InvariantCulture, $"""
        # Written by `otel-stack clickhouse up`; runs only between `up` and `down`.
        receivers:
          otlp:
            protocols:
              http:
                endpoint: {Host}:{CollectorPort}
        processors:
          batch:
            timeout: 5s
            send_batch_size: 5000
        exporters:
          clickhouse:
            endpoint: tcp://{Host}:{TcpPort}?dial_timeout=10s
            database: {Database}
            ttl: {TtlHours}h
            create_schema: true
            timeout: 10s
            retry_on_failure:
              enabled: true
        service:
          telemetry:
            metrics:
              level: none
            logs:
              level: warn
          pipelines:
            traces:
              receivers: [otlp]
              processors: [batch]
              exporters: [clickhouse]
            metrics:
              receivers: [otlp]
              processors: [batch]
              exporters: [clickhouse]
            logs:
              receivers: [otlp]
              processors: [batch]
              exporters: [clickhouse]

        """));

    // A directory as ClickHouse wants it: escaped, forward slashes, one trailing slash.
    private static string Dir(string path) => Escape(CollectorConfig.Posix(path).TrimEnd('/') + "/");

    // What xml.sax.saxutils.escape escapes.
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    // A raw string takes the source file's line endings, CRLF on a Windows checkout; the configs are always LF.
    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
