# Serving JSON from `rig web`

`rig web` is one server (`rig.web_host.server.StudioServer`) that serves the SPA in `web/`, the
legacy studio, and JSON for any feature that wants a page. Features never import the server, and the
server never imports a feature. A feature exposes a **route**, and one composition root mounts it.

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
2. Build the payload from typed view models (dataclasses) and serialise with
   `rig.typed_json.encode_json`. Do not build dicts: the ratchet bans string-key reads. The
   TypeScript types the SPA declares for the payload are the other half of the contract.
3. Wrap the work in `respond(handler, produce, unavailable=(...))`:
   - `RouteNotFoundError` gives 404, for a job, run or file that does not exist.
   - `ValueError` gives 400, for bad input; `Request.int_param` raises it for you.
   - The `unavailable` exceptions give 503, for a store or service the route needs that is down.
   - Anything else is a bug and propagates.
4. For a write (POST), read the body only through `json_body(handler)`. It refuses requests that did
   not come from the dashboard on this machine.
5. Add the route to the tuple in `mounted_routes()`. Import inside the function, so `rig web --help`
   stays fast and the import-linter contracts hold: `desktop_manager` may import `rig`, and features
   in `rig` stay independent of one another.
6. If the feature has a page, add it under `/ops/<page>` in `web/router.ts`. `serve_app` already
   serves the SPA document for every `/ops/` path.

## Testing

Test the view builders directly. To test the HTTP surface, start a `StudioServer` on port 0 with
just your route and call it, as `tests/test_web_routes.py` does. Browser tests of a page mount a
fake route that serves canned JSON, so they need no real data on the machine.

## Why not import the feature into the server

`server.py` still calls a few older handlers directly (`rig.connectivity.web`, `rig.triage.web`).
Each one makes the server depend on that feature, so the server cannot be tested without it and
`rig` features stop being independent. With the composition root, a feature depends only on
`rig.web_routes`, and adding a page costs one route object plus one line in `mounted_routes()`.
New routes use this pattern; move the older handlers across when you next touch them.
