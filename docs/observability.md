# Observability

CLI tools in rig record traces with `rig.tracing`, a small
stdlib-only span writer. Spans are buffered in memory and appended as OTLP-JSON
NDJSON on process exit (or every few hundred spans). There is no OpenTelemetry SDK,
collector, or external service.

| Setting                       | Default                                  |
| ----------------------------- | ---------------------------------------- |
| Output file                   | `temp/traces/sdk/<tool>/<YYYY-MM-DD>.ndjson` (repo root) |
| `SHARED_UTILITIES_TRACE_DIR`  | overrides `temp/traces/sdk`              |
| `SHARED_UTILITIES_TRACE_HOME` | replaces only the default root           |
| `SHARED_UTILITIES_TRACING=0`  | turns tracing off                        |

Each line is one `resourceSpans` envelope (`service.name` = the tool). Each process
run is one trace. Tracing never breaks the tool: write errors are swallowed.

## Wired tools

- `estate`: root span per command, a span per clone, every `git`/`gh` subprocess.
- Every `git` and `gh` call anywhere in rig: one span each (`scope` `rig.proc.git` /
  `rig.proc.gh`, with the command, cwd, exit code, duration and a stderr snippet on
  failure), because they all go through `rig.proc`, which runs on `tracing.run`.
- `desktop-manager`: root span per command (`desktop-manager workspace restore`). A
  restore nests:
  - `workspace.restore` (dry run, flags, desktop count, retired desktops)
  - phases: `restore.settle_app_updates`, `restore.plan`,
    `restore.start_terminal_command_upgrades`, `restore.ensure_desktop_count`,
    `restore.retire_desktops`, `restore.second_pass`, `restore.return_to_start`
  - `restore.desktop` per desktop, then `restore.desktop_attempt` per retry, then
    `restore.entry.launch` / `restore.entry.reuse` per entry, then
    `restore.launch_entry`, `win32.apply_window_placement`, and helper subprocesses.
  - Virtual-desktop calls: `win32.ensure_desktop_count`, `win32.rename_desktop` (with
    the previous name), `win32.retire_switched_off_desktops`, `win32.go_to_desktop`,
    `win32.ensure_current_desktop`.
  The restore log (`%APPDATA%\workspace-manager\logs\`) writes a `Trace spans:` line
  pointing at that run's trace file.

## Running processes

Run `git` and `gh` through `rig.proc` (`git`, `git_out`, `gh`, `run_gh`), never `subprocess`
directly. Each call is a span; stdin is closed; output is UTF-8 with replacement; there is no console window on Windows. A call whose working
directory is under `<code root>/external` or `<code root>/other` is hardened: hooks,
fsmonitor, `ext::` transports and submodule recursion are off, and git never prompts.

## Adding spans

```python
from shared_utilities import tracing

def main() -> None:
    tracing.init_tracing("my-tool")          # names the tool, starts a trace
    with tracing.span("my-tool run", scope="my_tool.cli", **{"cli.argv": sys.argv[1:]}):
        app()

with tracing.span("step", scope="my_tool", **{"item.name": name}) as span:
    span.set(**{"item.result": result})     # attributes can be added until it ends

@tracing.traced("my_tool.helper", scope="my_tool")
def helper() -> None: ...

proc.git("status", cwd=repo)                 # git/gh: use rig.proc (span + hardening)
tracing.run(["uv", "sync"], capture_output=True, text=True)  # other commands: subprocess.run, as a span
tracing.submit(pool, fn, arg)                # pool.submit, carrying the current span
threading.Thread(target=contextvars.copy_context().run, args=(fn, arg))  # same for bare threads
```

Tests get an autouse fixture (`tests/conftest.py::isolate_trace_output`) that points
`SHARED_UTILITIES_TRACE_DIR` at a temp directory, so suite runs never write to the
repo's trace files.

## C# tools

`Rig.Core.Otel.TraceFileWriter` writes the same day files from .NET: an `ActivityListener`
over the `ActivitySource`s it is given, each source mapped to the scope name its spans
are written under. It honours the variables above plus `SHARED_UTILITIES_TRACE_HOME`
(replaces only the default root), appends one envelope per write, and writes when
2000 spans are buffered, on `Flush`, every interval after `FlushPeriodically`, and on
`Dispose`. `Flush(includeOpen: true)` also writes each running span as it stands,
marked `span.incomplete`; readers keep the finished copy once it lands. A span's
trace id is its `Activity`'s, so each root activity is its own trace. Status `Error`
becomes `code: 2` with the status description as the message.

Chrome spans keep the Python names (`chrome_tabs.<step>` under scope
`desktop_manager.chrome`), so `chrome-tabs traces` reads either language's runs.

## Inspecting traces (`rig traces`)

`rig traces` reads the same day files and answers the questions a failed background session raises. It parses the
JSON stream directly, so no embedded DuckDB is needed; the DuckDB recipe below remains for ad-hoc SQL.

```bash
rig traces runs [--limit 20] [--tool <service.name>] [--since 24h] [--errors-only]
rig traces summary [--since 24h] [--json]
```

- `runs` lists the newest runs first: tool, root command, start, run duration, span count and failed-span count.
  `--tool` keeps one `service.name`; `--since` keeps runs with a span that ended inside the window.
- `--errors-only` keeps only runs with a failed span and, after the table, prints each failure's exact `traceId`,
  `spanId`, `service.name`, span name, `process.command`, `process.cwd`, `process.exit_code` (or the C# root
  span's `process.exit.code`), `process.stderr_snippet` and the most specific error message. A failure is a span
  whose `status.code` is not 1 or whose traced process has a non-zero exit code.
- `summary` counts runs, spans and failed spans, reports span and run duration `p50`/`p90`/`p99`, and breaks the
  window down by tool and command, with the most recent errors.

The folder is `temp/traces/sdk` in this checkout. `SHARED_UTILITIES_TRACE_DIR` replaces it, and
`SHARED_UTILITIES_TRACE_HOME` replaces only the default, the same resolution `TraceFileWriter` uses. A missing
folder, an empty file, a torn last line and a corrupt JSON line are skipped rather than failing the report; the
same span written twice (an incomplete flush and its complete copy) is counted once, preferring the complete copy.

### `rig traces summary --json`

```json
{
  "root": "/code/personal/rig/temp/traces/sdk",
  "since": "2026-03-03T10:30:00Z",
  "window": "24h",
  "runs": 2, "spans": 4, "failures": 2, "failure_rate": 0.5,
  "durations_ms": { "p50": 1500.0, "p90": 3000.0, "p99": 3000.0 },
  "run_durations_ms": { "p50": 2000.0, "p90": 3000.0, "p99": 3000.0 },
  "tools": [ { "name": "estate", "runs": 1, "spans": 3, "failures": 0 } ],
  "commands": [ { "name": "git status", "runs": 1, "spans": 2, "failures": 0 } ],
  "errors": [ { "trace_id": "…", "span_id": "…", "tool": "desktop-manager", "span": "rig gh pr wait" } ]
}
```

`since` is UTC, `failure_rate` is failed spans over all spans, and percentiles are nearest-rank milliseconds.
`tools` and `commands` are ordered by spans, then failures, then name; `errors` is newest first. Every error
also carries `command`, `cwd`, `exit_code`, `status_code`, `stderr`, `exception`, `exception_message` and
`message`. An absent process fact is `null` (or an empty string for the snippets), never omitted.

## Querying traces (DuckDB)

```python
import duckdb

con = duckdb.connect()
con.execute("""
    SELECT
        sp.name,
        sp.traceId,
        (CAST(sp.endTimeUnixNano AS BIGINT) - CAST(sp.startTimeUnixNano AS BIGINT)) / 1e6 AS duration_ms,
        sp.status.code AS status_code
    FROM read_json_auto('temp/traces/sdk/desktop-manager/*.ndjson'),
         UNNEST(resourceSpans) AS t(rs),
         UNNEST(rs.scopeSpans) AS t2(ss),
         UNNEST(ss.spans) AS t3(sp)
    ORDER BY sp.startTimeUnixNano DESC
    LIMIT 20
""").df()
```
