# Local OpenTelemetry stack

Rig runs a local OpenTelemetry collector so any tool on this machine can send
traces, metrics and logs to one place: a native core collector writing rotated
files, built to cost nearly nothing while idle. The files are the record; anything
richer (a dashboard, SQL) reads or replays them.

```bash
uv run otel-stack collector install          # download pinned otelcol, verify sha256, unpack
uv run otel-stack collector service install  # macOS: LaunchAgent, starts now and at login
uv run otel-stack collector status           # binary, ports, memory, data folder
uv run otel-stack collector ensure           # install if missing, start if not listening (idempotent)
uv run otel-stack collector stop | start | config --print
uv run otel-stack collector forward set <url> | off | show  # also send to another collector
```

## What runs

| | |
|---|---|
| Binary | official **core** `otelcol` 0.162.0, sha256 pinned per platform in `src/Rig.Core/Otel/OtelPins.cs` |
| Listens | OTLP gRPC `127.0.0.1:4317`, OTLP HTTP `127.0.0.1:4318` (loopback only) |
| Pipeline | `otlp` → `batch` (10 s) → `file/<signal>` for traces, metrics, logs |
| Data | `<rig data>/otel/data/<signal>.jsonl` (OTLP JSON, one batch per line) |
| Config | `<rig data>/otel/otelcol.yaml`, regenerated on every start |
| Log | `<rig data>/logs/otelcol.log` |

`<rig data>` is `~/Library/Application Support/shared-utilities` on macOS and
`%LOCALAPPDATA%\shared-utilities` on Windows.

**macOS**: the `dev.rtkelly.rig.otelcol` LaunchAgent (`RunAtLoad`, `KeepAlive`,
`ThrottleInterval` 60, `ProcessType` Background, low-priority IO, `Nice` 10).
**Windows**: the jobs coordinator's `otel-collector` job runs `collector ensure` at
logon and every tick, starting the binary detached with no console window only
when nothing listens on 4318.

## Why core, not contrib

Measured on an M-series Mac with this config, two idle minutes then 100,000 spans:

| | resident after start | resident idle | CPU, 2 idle minutes | CPU, 100k spans |
|---|---|---|---|---|
| core `otelcol` | 22 MB | 3 MB | 0.03 s | 0.47 s |
| contrib `otelcol-contrib` | 131 MB | 37 MB | 0.04 s | 0.41 s |

Core has everything the always-on half needs. The config keeps the idle process
asleep: no `memory_limiter` (its check timer never stops), the collector's own
metrics turned off (`service.telemetry.metrics.level: none`), and `batch` whose
timer only runs while data waits.

## Retention: 7 days

The file exporter rotates at 100 MB, keeping at most 50 rotated files and 7 days.
It only prunes when it rotates, which a quiet week may never trigger, so
`collector ensure` (every tick, via the `otel-collector` job) also deletes rotated
files last written more than 7 days ago. The active `<signal>.jsonl` is never
deleted.

## Sending telemetry to it

Any OTLP exporter:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

Claude Code (metrics and events):

```bash
export CLAUDE_CODE_ENABLE_TELEMETRY=1
export OTEL_METRICS_EXPORTER=otlp
export OTEL_LOGS_EXPORTER=otlp
export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

Rig's own tracer (`rig.tracing`) still writes NDJSON day files; it does not send
to the collector yet.

## Owning the port

This collector is meant to be the machine's one OTLP entry point on 4317/4318.
`ensure` and `start` check who holds 4318 (`lsof` on macOS, `netstat -ano` on
Windows) and, when it is not `otelcol`, print the holder and exit **3** rather
than reporting "listening". On Windows a loopback bind can succeed alongside
Docker's wildcard `0.0.0.0:4318`, so a merely open port proves nothing.
`collector status` names the holder of each port.

## Forwarding to another collector

The files stay the record; forwarding adds one bounded `otlp_http/forward`
exporter to every pipeline, for a dashboard such as a Docker collector feeding
Aspire:

```bash
uv run otel-stack collector forward set http://127.0.0.1:24318   # OTLP/HTTP base URL
uv run otel-stack collector forward show | off
uv run otel-stack collector stop && uv run otel-stack collector start  # apply
```

The endpoint lives in `<rig data>/otel/forward`. When the target is down the
exporter retries for 30 s, queues at most 200 batches and then drops, so a dead
dashboard never costs the files anything. A Docker collector that used to
publish 4317/4318 moves to another host port (24317/24318 here) so the two never compete.

## ClickHouse on demand

```bash
uv run otel-stack clickhouse up         # install if missing (pinned, verified), start both, wait until they answer
uv run otel-stack clickhouse backfill   # load the recorded files; safe to rerun
uv run otel-stack clickhouse query "SELECT ServiceName, count() FROM otel.otel_traces GROUP BY ServiceName"
uv run otel-stack clickhouse status     # each process, its memory, rows and disk per table
uv run otel-stack clickhouse down       # stop both and wait for them to exit; the data stays
```

This replaces #275's Docker stack with native binaries, so it needs no Docker.

| | |
|---|---|
| ClickHouse | official `v26.8.23.6-lts` single binary, sha256 pinned in `src/Rig.Core/Otel/OtelPins.cs`; HTTP `127.0.0.1:18123`, native `127.0.0.1:19000`, user `default` from loopback only |
| Collector | `otelcol-contrib` 0.162.0 (same release as core, sha256 pinned), OTLP HTTP `127.0.0.1:14318`, the `clickhouse` exporter into database `otel` |
| Retention | the exporter creates every `otel` table with a **7-day TTL** |
| Files | configs, pids and backfill state in `<rig data>/otel/clickhouse/`, data in its `data/`; logs in `<rig data>/logs/clickhouse.log` and `otelcol-contrib.log` |

**Loading data.** Nothing streams into ClickHouse all day. `backfill` reads what
the always-on collector and rig's tracer already recorded:

- **Traces** from rig's day files and the collector's `traces*.jsonl`. Spans are
  compared with `otel.otel_traces` by trace and span id, so a rerun, or the
  collector renaming its file on rotation, never sends a span twice. Spans that
  ended in the last 5 minutes wait for the next run, and a file is only marked
  done once ClickHouse holds every span in it.
- **Logs and metrics** from the collector's `logs*.jsonl` and `metrics*.jsonl`.
  These have no identity to compare, so each file is tracked by inode and byte
  offset. A rotated file keeps its inode, so nothing is resent. A rejection part
  way through resumes from the last accepted line.

**Idle cost.** Measured on an M-series Mac over two idle minutes after `up`:

| | resident | CPU, 2 idle minutes |
|---|---|---|
| ClickHouse, these settings | 11 MB | 0.68 s |
| ClickHouse, #275's low-memory settings alone | 30 MB | 6.24 s |
| `otelcol-contrib` | 2 MB | 0.07 s |

The difference is wake-ups, each found by sampling the idle server:

- The system log tables are left **out** of the config, not marked `remove="1"`.
  In a standalone config there is nothing for `remove` to remove, so the empty
  section it leaves turns each log on with its defaults: nine tables and a
  once-a-second collector.
- The global sampling profiler is off.
- Config polling is hourly instead of every 2 s.
- The schedule pools have 1–2 threads instead of 16.
- Asynchronous metrics refresh every 60 s.
- The watchdog parent process is off (`CLICKHOUSE_WATCHDOG_ENABLE=0`), so there
  is one process and its pid is the server's.

Backfilling 20,000 spans (a 4 MB day file) took 3.8 s. A rerun took 0.24 s and
sent nothing.

**Windows.** ClickHouse publishes no native Windows build; it runs there only
under WSL or Docker. `up` says so instead of half-starting. Nothing in this half
is scheduled on any platform.

**HyperDX** is left out: it needs Docker and MongoDB. Query with
`otel-stack clickhouse query`, or point any ClickHouse client at
`http://127.0.0.1:18123`.

## Recent traces in `rig web`

`rig web` serves two read-only endpoints for its ops pages (`rig.otel_stack.web`):

- `GET /api/ops/telemetry/status`: whether ClickHouse answers, and the explorer UIs with trace-link templates.
- `GET /api/ops/telemetry/traces?days=1..30&limit=1..500&service=`: recent traces, newest first.

Traces come from this ClickHouse when it answers (checked at most every 30 s). When it does not, they come from
rig's own NDJSON day files (the roots backfill reads, only those dated within `days`) and the response's `note`
says so. The always-on collector's `traces*.jsonl` are not read there: they run to gigabytes a day, so backfill
them into ClickHouse to see them.

Trace links go to HyperDX when `RIG_HYPERDX_URL` names one (unset: no HyperDX links) and to Aspire at
`RIG_ASPIRE_URL` (default `http://localhost:18888`; set it empty to turn the link off). Each UI is probed at most
every 30 s.
