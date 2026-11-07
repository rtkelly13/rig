"""The always-on collector's config: OTLP in on loopback, one rotating JSON-lines file per signal out.

Everything here is chosen for an idle laptop on battery:

- receivers bind 127.0.0.1 only;
- `batch` (10 s) is the only processor, and its timer runs only while data waits;
- `service.telemetry.metrics.level: none` turns off the collector's own metrics
  endpoint and the periodic scrape behind it; logs are `warn`;
- no `memory_limiter`, whose check interval would wake the process forever.

Rotation (`max_days` / `max_backups`) only prunes when a file rotates, so quiet
days can leave old rotated files behind; `collector ensure`, every tick, also
deletes any older than 7 days (`retention`).

Paths are written with forward slashes inside single quotes, with any apostrophe
doubled (YAML's only escape in that style), so `/Users/O'Brien/...` stays valid.
Windows accepts forward slashes.

**Forwarding (optional).** With a forward endpoint set (`otel-stack collector
forward set <url>`), every pipeline also sends to that OTLP/HTTP endpoint, such as a
Docker collector feeding Aspire, ClickHouse or HyperDX. The files stay the record:
each exporter has its own queue, so a downstream that is down never holds up the
files. The queue is small and retries give up after 30 s, so an absent downstream
costs a little memory and some refused connections, and its data is dropped.
"""

from __future__ import annotations

import pathlib

from rig.otel_stack.paths import SIGNALS, active_file, data_dir

HOST = "127.0.0.1"
GRPC_PORT = 4317
HTTP_PORT = 4318
MAX_MEGABYTES = 100
MAX_DAYS = 7
MAX_BACKUPS = 50
FORWARD_EXPORTER = "otlp_http/forward"
#: Batches held while the downstream is unreachable; beyond this they are dropped.
FORWARD_QUEUE_SIZE = 200
FORWARD_GIVE_UP = "30s"


def yaml_quoted(text: str) -> str:
    """*text* as a YAML single-quoted scalar."""
    return "'" + text.replace("'", "''") + "'"


def render(
    *,
    data: pathlib.Path | None = None,
    host: str = HOST,
    grpc_port: int = GRPC_PORT,
    http_port: int = HTTP_PORT,
    forward: str | None = None,
) -> str:
    folder = data or data_dir()
    exporters = "".join(
        f"""  file/{signal}:
    path: {yaml_quoted(active_file(signal, folder).as_posix())}
    rotation:
      max_megabytes: {MAX_MEGABYTES}
      max_days: {MAX_DAYS}
      max_backups: {MAX_BACKUPS}
"""
        for signal in SIGNALS
    )
    if forward is not None:
        exporters += f"""  {FORWARD_EXPORTER}:
    endpoint: {yaml_quoted(forward)}
    timeout: 2s
    retry_on_failure:
      enabled: true
      initial_interval: 1s
      max_interval: 5s
      max_elapsed_time: {FORWARD_GIVE_UP}
    sending_queue:
      enabled: true
      num_consumers: 1
      queue_size: {FORWARD_QUEUE_SIZE}
"""
    extra = f", {FORWARD_EXPORTER}" if forward is not None else ""
    pipelines = "".join(
        f"""    {signal}:
      receivers: [otlp]
      processors: [batch]
      exporters: [file/{signal}{extra}]
"""
        for signal in SIGNALS
    )
    return f"""# Written by `otel-stack collector config`; regenerated on every start. See docs/otel-stack.md.
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: {host}:{grpc_port}
      http:
        endpoint: {host}:{http_port}
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
{pipelines}"""
