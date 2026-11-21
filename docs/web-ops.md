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

`rig tools install web` registers the dashboard as a background service on port 8090 (a LaunchAgent on
macOS, a logon task on Windows, a systemd user unit on Linux) and restarts it. `rig tools ensure web`
starts it only when nothing answers on the port yet, then waits for it, so it is safe to run repeatedly:
the Work desktop runs it before opening the triage inbox as a Chrome app window on screen 2. The
dashboard replaced the old `prs-to-review.md` checklist. `rig web install|ensure|uninstall|backup|migrate|db-status`
remain as aliases. `rig tools install mcp` runs the same host on port 8092 for its HTTP MCP transport,
while `rig web mcp` keeps serving the tools over stdio for a local client.

For several agents, share the existing web host at `http://127.0.0.1:8090/api/mcp`
before installing a second host. [MCP tooling](mcp-tooling.md) covers measured
footprints, process lifetimes and the opt-in companion setup.

On Windows the logon task is registered from XML (`ServiceTaskXml`) and starts rig the way the jobs tasks do, with
the same `--launcher pythonw|direct` and `--pythonw` options on `install` and `ensure` ([Launchers](desktop-manager/jobs.md#launchers)), so no
console window stays open for the dashboard. A long-lived service (web, mcp) has no time limit and is restarted
a minute after it fails, up to ten times; desktop and collector run once at sign-in. Install from the runtime copy
(`rig` on PATH): the task runs the rig that installed it, in the folder it was installed from.

## How the pieces fit

- Telemetry, the decisions feed, disk usage, trimming and the jobs coordinator are C# routes
  (`Rig.Web.TelemetryRoutes`, `Rig.Web.EstateOpsRoutes`, and `Rig.Web.JobsRoutes`). All feature routes
  are served directly by the ASP.NET Core host in `Rig.Web`.
- Every error is a JSON `{"error": ...}` with 400, 404 or 503. The writes
  (`jobs/run`, `trim/apply`) require a loopback `Host`, a matching `Origin` and `application/json`.
- Run now answers at once: the run is a detached `rig jobs run --trigger tick` (`rig.tracing.spawn_detached`)
  and shows on Runs when it finishes. The coordinator lock makes it skip while a scheduled run holds it.
  It replaces the old studio panel and its blocking `POST /api/jobs/run`, which are gone.
- Trace links are built in the browser from the `*_trace_url` templates the telemetry status
  returns (`{trace_id}` is substituted), so jobs never need to know where HyperDX or Aspire live.
  HyperDX comes from `RIG_HYPERDX_URL`; Aspire from `RIG_ASPIRE_URL` (default
  `http://localhost:18888`, empty to disable).
- Attention is computed client-side from the jobs, runs and decisions answers.

The pages live in `web/Ops*.tsx` with the client in `web/opsApi.ts`; `OpsBrowserTests` in
`tests/Rig.Web.BrowserTests` drives them against the same JSON contract, supplying every answer with
Playwright routes.

## For agents: `rig web mcp`

`rig web mcp` serves the same data as an MCP server on stdio (`Rig.Web.Mcp.RigOpsTools` over
`Rig.Core`). `rig web` also maps the same tools at `POST /api/mcp` for an HTTP client. Each tool calls
the view builder its `/api/ops` route calls, so an agent and the pages never disagree. The stdio server
does not need `rig web` to be running.

```bash
claude mcp add rig-ops -- rig web mcp
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
