"""Config for the on-demand pair: a laptop-sized ClickHouse, and the contrib collector feeding it.

ClickHouse (ported from #275's `infra/otel/clickhouse/low-memory.xml`, minus Docker):

- binds 127.0.0.1 only, HTTP on `HTTP_PORT` (the hub's #275 default) and native TCP
  on `TCP_PORT`; no interserver port;
- caps server memory at 1 GiB and shrinks the caches that default to gigabytes;
- a 2-thread background pool, and asynchronous metrics refreshed every 60 s
  instead of every second: the 1 s default is the main thing that keeps an idle
  server awake;
- no system log tables at all. They are left **out** of the config, not marked
  `remove="1"`: in a standalone config there is nothing for `remove` to remove,
  so `<metric_log remove="1"/>` stays in as an empty section, which turns the log
  on with its defaults (measured: nine log tables and a once-a-second collector);
- the global sampling profiler off, config polling hourly instead of every 2 s,
  and the schedule pools at 1-2 threads instead of 16;
- logs to the console at warning, which `clickhouse.up` sends to one log file.

Measured on an M-series Mac, two idle minutes after start: 0.42 s of CPU and
10 MB resident, against 6.24 s and 30 MB with #275's settings alone.

The contrib collector receives OTLP/HTTP on loopback `COLLECTOR_PORT` (backfill
posts there) and writes through the `clickhouse` exporter, which creates the
`otel` tables with a **7-day TTL**. It is not the always-on collector: that one
stays the core build writing files, and this one exists only between `up` and
`down`.
"""

from __future__ import annotations

import pathlib
from xml.sax.saxutils import escape

HOST = "127.0.0.1"
HTTP_PORT = 18123
TCP_PORT = 19000
COLLECTOR_PORT = 14318
DATABASE = "otel"
TTL_HOURS = 7 * 24
MAX_SERVER_MEMORY = 1 << 30


def _dir(path: pathlib.Path) -> str:
    """A directory path as ClickHouse wants it: escaped, forward slashes, trailing slash."""
    return escape(path.as_posix().rstrip("/") + "/")


def server_xml(data: pathlib.Path, users: pathlib.Path) -> str:
    return f"""<?xml version="1.0"?>
<!-- Written by `otel-stack clickhouse up`; regenerated on every start. -->
<clickhouse>
    <logger>
        <level>warning</level>
        <console>1</console>
    </logger>
    <listen_host>{HOST}</listen_host>
    <http_port>{HTTP_PORT}</http_port>
    <tcp_port>{TCP_PORT}</tcp_port>
    <path>{_dir(data)}</path>
    <tmp_path>{_dir(data / "tmp")}</tmp_path>
    <user_files_path>{_dir(data / "user_files")}</user_files_path>
    <format_schema_path>{_dir(data / "format_schemas")}</format_schema_path>
    <user_directories>
        <users_xml><path>{escape(users.as_posix())}</path></users_xml>
    </user_directories>

    <max_server_memory_usage>{MAX_SERVER_MEMORY}</max_server_memory_usage>
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
"""


def users_xml() -> str:
    """One passwordless `default` user, reachable from loopback only, with a small per-query memory cap."""
    return """<?xml version="1.0"?>
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
"""


def collector_yaml() -> str:
    """The contrib collector: OTLP/HTTP in on loopback, the clickhouse exporter out, 7-day TTL."""
    return f"""# Written by `otel-stack clickhouse up`; runs only between `up` and `down`.
receivers:
  otlp:
    protocols:
      http:
        endpoint: {HOST}:{COLLECTOR_PORT}
processors:
  batch:
    timeout: 5s
    send_batch_size: 5000
exporters:
  clickhouse:
    endpoint: tcp://{HOST}:{TCP_PORT}?dial_timeout=10s
    database: {DATABASE}
    ttl: {TTL_HOURS}h
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
"""
