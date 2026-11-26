# Serving JSON from `rig web`

`rig web` is one ASP.NET host (`src/Rig.Web`) that serves the SPA in `web/` and every JSON route it calls. Nothing is
forwarded and no Python process is started: the Python web server, its route protocol and the child that mounted
the routes are deleted.

## The C# host

- A bare `rig web` (`--port`, `--host`, `--open/--no-open`, `--preview`) runs on the C# CLI
  (`src/Rig.Cli/Web/WebCommand.cs`); the legacy service and database subcommands (`install`, `ensure`,
  `backup`, `migrate`, `db-status`, `uninstall`) are aliases for `rig tools`; `mcp` runs the C# ops tools over stdio.
- The app's document, `/static/` and `/media/assets/` are `Rig.Web.FrontendRoutes`, sent with the
  headers and content types the old Python server sent. Two answers differ from Python's: a missing file or document
  is a 404 with the `{"error": ...}` envelope rather than an HTML error page, and a bare `/repos` is
  the app's document (Python had only `/repos/` deep links). Kestrel removes `.` and `..` segments
  before routing, so `/static/../x` is not a static path at all and gets Kestrel's empty 404; an encoded `%2F` or `..`
  inside a file name is refused when it leaves the folder.
- The C4 viewer (`Rig.Web.C4.C4Routes`) serves its page and static files from `src/Rig.Web/C4/static` and the
  bundle JSON from `Rig.Core.C4`. `?bundle=` picks one of the sample bundles beside `experiments/c4-okf/rig` or the
  one `RIG_C4_BUNDLE` names; the bundle is re-read on every request. Notes are rendered with Markdig with raw HTML
  disabled and script links left as text. Against the Python viewer it answered byte-identical JSON for every
  request replayed (see `tests/Rig.Web.Tests/Recorded/c4`), except where noted in the tests.

## Route inventory

| Method | Path | Answer | Served by |
|:-------|:-----|:-------|:----------|
| GET | `/api/status` | `{environment, port, is_preview, db_path}` | C# `Rig.Web.HostRoutes` |
| GET | `/api/dev/mtime` | `{mtime, css_mtime, is_preview}` over the SPA and studio files | C# `Rig.Web.HostRoutes` |
| GET | `/api/estate` | `{projects: [...]}` from the managed-projects registry, brand assets and Orca badges | C# `Rig.Web.HostRoutes` |
| GET | `/`, `/index.html`, `/estate`, `/studio`, `/optical`, `/triage`, `/ops`, and `/triage/`, `/repos/`, `/estate/`, `/ops/` deep links | the SPA document | C# `Rig.Web.FrontendRoutes` |
| GET | `/api/estate/graph`, `/api/estate/graph/refresh` | the connectivity graph, a snapshot under a minute old or rebuilt (`no-store`; 503 `{error}`) | C# `Rig.Web.ConnectivityRoutes` |
| GET | `/static/triage.html` | 302 to `/triage/inbox` | C# `Rig.Web.FrontendRoutes` |
| GET | `/static/...` | files under `src/Rig.Web/templates` | C# `Rig.Web.FrontendRoutes` |
| GET | `/media/assets/...` | read-only brand assets | C# `Rig.Web.FrontendRoutes` |
| POST | `/api/db/backup` | `{status, backup_file}` | C# `Rig.Web.HostRoutes` |
| GET | `/api/triage` | `{entries, queue_path, skipped}`; `skipped` lists repositories a refresh found missing on GitHub | C# `Rig.Web.TriageRoutes` |
| POST | `/api/triage/refresh`, `/api/triage/watch`, `/api/triage/interact` | the queue again | C# `Rig.Web.TriageRoutes` |
| GET | `/c4`, `/c4/`, `/c4/static/...` | the C4 viewer | C# `Rig.Web.C4.C4Routes` |
| GET | `/api/c4/bundle`, `/api/c4/concept`, `/api/c4/expand` | C4 bundle JSON | C# `Rig.Web.C4.C4Routes` |
| GET | `/api/ops/decisions`, `/api/ops/estate/disk`, `/api/ops/estate/trim` | decisions feed, disk usage, trim scan | C# `Rig.Web.EstateOpsRoutes` |
| POST | `/api/ops/estate/trim/apply` | the paths trimmed | C# `Rig.Web.EstateOpsRoutes` |
| GET | `/api/ops/telemetry/status`, `/api/ops/telemetry/traces` | trace store status and recent traces | C# `Rig.Web.TelemetryRoutes` |
| GET | `/api/ops/jobs`, `/api/ops/runs`, `/api/ops/runs/<job>/<run>/output` | scheduled jobs, their runs and run output | C# `Rig.Web.JobsRoutes` |
| POST | `/api/ops/jobs/run` | starts a job | C# `Rig.Web.JobsRoutes` |

## Adding a route

1. Add a static class in `src/Rig.Web` with a `Map(IEndpointRouteBuilder, ...)` method and call it from
   `RigWebHost.CreateAsync`. Claim only paths under `/api/ops/<feature>/...`.
1. Build the payload from records and serialise with the feature's own `JsonSerializerOptions`
   (see `OpsJson`): snake_case keys, nulls kept. The TypeScript types the SPA declares for the payload are the other
   half of the contract.
1. Failures use the `{"error": ...}` envelope: 404 for a job, run or file that does not exist, 400 for bad input,
   503 for a store or service the route needs that is down.
1. For a write (POST), read the body only through the guard in `OpsJson`, which refuses requests that did
   not come from the dashboard on this machine.
1. If the feature has a page, add it under `/ops/<page>` in `web/router.ts`. The host already
   serves the SPA document for every `/ops/` path.

## Testing

The routes are tested in `tests/Rig.Web.Tests`: `OpsRoutesTests` and `C4RoutesTests` drive the HTTP surface over fakes
and recorded answers, and `OpsCoreTests` the view builders. Browser tests of a page canned the API answers with
Playwright routes, so they need no real data on the machine.

The browser tests are C# Playwright tests in `tests/Rig.Web.BrowserTests`. They drive an in-process
`RigWebHost` over a temp tree that holds a copy of the frontend, as the browser talks to the C# host in
production. A test that draws the estate graph lays out a registry and git checkouts under the temp
`CODE_ROOT` (`Estates`); the triage tests seed the shared database through `QueueStore`, and the ops tests
supply the C# answers with Playwright routes. One Chromium is shared by the assembly and each test gets its
own context. Install the browser once with `playwright.ps1` (see [development.md](development.md)); CI caches it.

