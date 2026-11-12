# Serving JSON from `rig web`

`rig web` is moving to one ASP.NET host (`src/Rig.Web`, #85 s25). Until every route group has moved,
the host owns the port and forwards each path it does not serve to the Python server
(`rig.web_host.server.StudioServer`), which still serves the SPA in `web/`, the legacy studio, and
JSON for the features below. In the Python server, features never import the server, and the server
never imports a feature. A feature exposes a **route**, and one composition root mounts it.

## The C# host and the forwarder

- A bare `rig web` (`--port`, `--host`, `--open/--no-open`, `--preview`) runs on the C# CLI
  (`src/Rig.Cli/Web/WebCommand.cs`); `Forwarded.RunsBare` lets the `web` group stay with Python for its
  subcommands (`install`, `ensure`, `backup`, `migrate`, `db-status`, `uninstall`, `mcp`) and `--help`.
  A bare `desktop-manager web` hands itself to C# too.
- The host starts `desktop-manager web upstream --port <free loopback port>` as a child, waits up to 60 s for it
  to listen, and closes the child's standard input to stop it; it signals only that child. If the
  child does not start, the host still serves its own routes and says so.
- The host answers its own routes first. Everything else goes over as it came: method, body and
  headers, `Host` and `Origin` included, so the Python write guard sees the browser's values. Only the
  target's path and query travel, always to the child's loopback address, and hop-by-hop headers
  (including any a `Connection` header names) are dropped both ways. Redirects are not followed and the
  answer otherwise comes back unchanged.
- Failures use the `{"error": ...}` envelope: 404 when no child is running (it failed to start or has
  exited), 502 when a running child does not answer.
- Each s25 slice moves a group into the host and `git rm`s its Python; the last one deletes the
  forwarder, `desktop-manager web upstream` and `StudioServer`.

## Route inventory

| Method | Path | Answer | Served by |
|:-------|:-----|:-------|:----------|
| GET | `/api/status` | `{environment, port, is_preview, db_path}` | C# `Rig.Web.HostRoutes` |
| GET | `/api/dev/mtime` | `{mtime, css_mtime, is_preview}` over the SPA and studio files | C# `Rig.Web.HostRoutes` |
| GET | `/api/estate` | `{projects: [...]}` from the managed-projects registry, brand assets and Orca badges | C# `Rig.Web.HostRoutes` |
| GET | `/`, `/index.html`, `/estate`, `/studio`, `/optical`, `/triage`, `/ops`, and `/triage/`, `/repos/`, `/estate/`, `/ops/` deep links | the SPA document | Python `rig.web_routes.serve_app` |
| GET | `/static/triage.html` | 302 to `/triage/inbox` | Python `rig.web_routes.serve_app` |
| GET | `/static/...` | files under `src/rig/web_host/templates` | Python `rig.web_host.server` |
| GET | `/media/assets/...` | read-only brand assets | Python `rig.web_host.server` |
| POST | `/api/db/backup` | `{status, backup_file}` | Python `rig.web_host.server` |
| GET | `/api/triage` | `{entries, queue_path}` | Python `rig.triage.web` |
| POST | `/api/triage/refresh`, `/api/triage/watch`, `/api/triage/interact` | the queue again | Python `rig.triage.web` |
| GET | `/c4`, `/c4/`, `/c4/static/...` | the C4 viewer | Python `rig.c4_okf.web` |
| GET | `/api/c4/bundle`, `/api/c4/concept`, `/api/c4/expand` | C4 bundle JSON | Python `rig.c4_okf.web` |
| GET | `/api/estate/graph`, `/api/estate/graph/refresh` | the connectivity graph, cached or rebuilt | Python `rig.connectivity.web` |
| GET | `/api/ops/decisions`, `/api/ops/estate/disk`, `/api/ops/estate/trim` | decisions feed, disk usage, trim scan | Python `rig.estate.web` |
| POST | `/api/ops/estate/trim/apply` | the paths trimmed | Python `rig.estate.web` |
| GET | `/api/ops/telemetry/status`, `/api/ops/telemetry/traces` | trace store status and recent traces | Python `rig.otel_stack.web` |
| GET | `/api/ops/jobs`, `/api/ops/runs`, `/api/ops/runs/<job>/<run>/output` | scheduled jobs, their runs and run output | Python `desktop_manager.jobs.web` |
| POST | `/api/ops/jobs/run` | starts a job | Python `desktop_manager.jobs.web` |

## The pieces

| Piece | Where | Job |
|:------|:------|:----|
| `Route` protocol | `src/rig/web_routes.py` | `(handler, request) -> bool`: answer and return True, or return False to pass |
| `Request` | `src/rig/web_routes.py` | method, path and query, parsed once; `param`, `int_param`, `flag` |
| `respond` | `src/rig/web_routes.py` | runs a producer and maps failures to the `{"error": ...}` envelope |
| `json_body` | `src/rig/web_routes.py` | the write guard: loopback Host, matching Origin, JSON content type, size cap |
| `mounted_routes()` | `src/desktop_manager/web_cli.py` | the composition root: the one place that imports several features |
| `StudioServer(routes=...)` | `src/rig/web_host/server.py` | offers each request to the mounted routes before its own handlers |

## Adding a route

1. In the feature, add a `web.py` holding a class or a module-level instance that satisfies `Route`.
   Claim only paths under `/api/ops/<feature>/...` and return False for everything else, so routes
   never fight over a path.
1. Build the payload from typed view models (dataclasses) and serialise with
   `rig.typed_json.encode_json`. Do not build dicts: the ratchet bans string-key reads. The
   TypeScript types the SPA declares for the payload are the other half of the contract.
1. Wrap the work in `respond(handler, produce, unavailable=(...))`:
   - `RouteNotFoundError` gives 404, for a job, run or file that does not exist.
   - `ValueError` gives 400, for bad input; `Request.int_param` raises it for you.
   - The `unavailable` exceptions give 503, for a store or service the route needs that is down.
   - Anything else is a bug and propagates.
1. For a write (POST), read the body only through `json_body(handler)`. It refuses requests that did
   not come from the dashboard on this machine.
1. Add the route to the tuple in `mounted_routes()`. Import inside the function, so `rig web --help`
   stays fast and the import-linter contracts hold: `desktop_manager` may import `rig`, and features
   in `rig` stay independent of one another.
1. If the feature has a page, add it under `/ops/<page>` in `web/router.ts`. `serve_app` already
   serves the SPA document for every `/ops/` path.

## Testing

Test the view builders directly. To test the HTTP surface, start a `StudioServer` on port 0 with
just your route and call it, as `tests/test_web_routes.py` does. Browser tests of a page mount a
fake route that serves canned JSON, so they need no real data on the machine.

The browser talks to the C# host, as it does in production. `tests/rig_web_host.py` starts the
built C# `rig web` on port 0 in front of the in-process `StudioServer`
(`RIG_WEB_PYTHON_ROUTES` names it, so the host starts no Python child), from a temp checkout that
holds a copy of the frontend and no registry. CI builds the CLI before pytest and passes it in
`RIG_CLI_DLL`; locally the first test to need it builds it into `temp/rig-web-tests/`.

## Why not import the feature into the server

`server.py` still calls a few older handlers directly (`rig.connectivity.web`, `rig.triage.web`).
Each one makes the server depend on that feature, so the server cannot be tested without it and
`rig` features stop being independent. With the composition root, a feature depends only on
`rig.web_routes`, and adding a page costs one route object plus one line in `mounted_routes()`.
New routes use this pattern; move the older handlers across when you next touch them.
