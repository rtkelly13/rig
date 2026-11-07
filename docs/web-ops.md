# Operations pages in `rig web`

`rig web` serves an **Operations** area under `/ops`, next to triage and the estate graph. It
replaces the standalone hub (port 4593) from PR #274: same panels, one server, one SPA.

| Page | Path | Reads |
|---|---|---|
| Attention | `/ops` | failed jobs (last run) and the worktree decisions feed |
| Jobs | `/ops/jobs` | `GET /api/ops/jobs`; **Run due jobs** and each row's **Run now** `POST /api/ops/jobs/run` |
| Runs | `/ops/runs?job=&run=` | `GET /api/ops/runs`, `GET /api/ops/runs/<job>/<run_id>/output` |
| Telemetry | `/ops/telemetry?days=` | `GET /api/ops/telemetry/status`, `GET /api/ops/telemetry/traces` |
| Decisions | `/ops/decisions` | `GET /api/ops/decisions` |
| Trim | `/ops/trim` | `GET /api/ops/estate/disk`, `GET /api/ops/estate/trim`, `POST /api/ops/estate/trim/apply` |

## Running it

`rig web install` registers the dashboard as a background service on port 8090 (a LaunchAgent on
macOS, a logon task on Windows) and restarts it. `rig web ensure` starts it only when nothing
answers on the port yet, then waits for it, so it is safe to run repeatedly: the Review desktop
runs it before opening the dashboard as a Chrome app window on screen 3, where the
`prs-to-review.md` checklist used to be.

## How the pieces fit

- Each feature owns its API: `rig.otel_stack.web`, `rig.estate.web` and
  `desktop_manager.jobs.web` each expose a `rig.web_routes.Route`. The composition root,
  `desktop_manager.web_cli.mounted_routes()`, hands them to `StudioServer(routes=...)`; the
  server imports none of them, so the `rig-features` independence contract holds.
- Every error is a JSON `{"error": ...}` with 400, 404 or 503. The writes
  (`jobs/run`, `trim/apply`) require a loopback `Host`, a matching `Origin` and `application/json`.
- Run now answers at once: the run is a detached `jobs run --trigger tick` (`rig.tracing.spawn_detached`)
  and shows on Runs when it finishes. The coordinator lock makes it skip while a scheduled run holds it.
  It replaces the old studio panel and its blocking `POST /api/jobs/run`, which are gone.
- Trace links are built in the browser from the `*_trace_url` templates the telemetry status
  returns (`{trace_id}` is substituted), so jobs never need to know where HyperDX or Aspire live.
  HyperDX comes from `RIG_HYPERDX_URL`; Aspire from `RIG_ASPIRE_URL` (default
  `http://localhost:18888`, empty to disable).
- Attention is computed client-side from the jobs, runs and decisions answers.

The pages live in `web/Ops*.tsx` with the client in `web/opsApi.ts`; `tests/test_web_ops_spa.py`
drives them against a fake route that answers the same JSON contract.

## For agents: `rig web mcp`

`rig web mcp` serves the same data as an MCP server on stdio (`desktop_manager.web_mcp`).
Each tool calls the view builder its `/api/ops` route calls, so an agent and the pages never
disagree. It does not need `rig web` to be running.

```bash
claude mcp add rig-ops -- uv run --directory <rig checkout> rig web mcp
```

| Tool | Same as | Changes anything |
|---|---|---|
| `attention` | the Attention page | no |
| `list_jobs`, `list_runs`, `run_output` | the jobs and runs routes | no |
| `start_run` | `POST /api/ops/jobs/run` | starts a detached run |
| `decisions`, `disk_usage` | the decisions and disk routes | no |
| `trim_candidates` | `GET /api/ops/estate/trim` | no (may start a background scan) |
| `apply_trim` | `POST /api/ops/estate/trim/apply` | deletes caches (destructive hint) |
| `telemetry_status`, `list_traces` | the telemetry routes | no |

Each tool carries MCP annotations (`readOnlyHint`, `destructiveHint`), so a client can auto-allow the
reads and ask before `apply_trim`. An expected refusal (unknown job, malformed run id) is a `ToolError`,
so the agent sees why; the routes answer the same with a 404 or 400. Like the trim page, `apply_trim`
only removes what the server's own last finished scan offered: call `trim_candidates` until
`scanning` is false first.
