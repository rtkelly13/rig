# MCP tooling and machine footprint

Rig MCP and its companions are opt-in. No MCP service is installed or started by this setup, and new Claude baselines no
longer add `chrome-devtools-mcp@latest`. Existing user registrations are preserved. The files under `config/mcp/` are
examples, not automatically loaded client configuration.

## Measurements (21 November 2026)

Apple M1, 8 GiB RAM, macOS/Darwin 27.2 arm64, Node 26.12.1, .NET SDK 11.0.100,
Debug build, C# MCP SDK 2.4. Three fresh-process runs per case, run sequentially
after builds/tests finished. Other users' agent sessions remained active. Swap
sampling near the end reported **5,688 MiB used**; RSS and startup timings are
especially sensitive to paging on this machine. These are local observations,
not controlled comparative performance guarantees.

Numbers below are medians of three runs. Idle RSS is the median of each run's five
samples, then the median across runs. Startup includes initialize and tools/list;
for multiple clients/copies it shows the first connection's startup. Parentheses
give the range across runs. CPU percentages use one core as 100%.

| Case | Startup ms | Idle RSS MiB | Early idle CPU % | Busy CPU % | Read-call median / p95 ms |
|---|---:|---:|---:|---:|---:|
| Rig compiled stdio, one | 1,081 (1,081–1,260) | 35 (35–35) | <0.2 | 71 | 0.59 / 2.63 |
| Rig compiled stdio, four copies total | 260 (249–711) | 359 (140–359) | <0.2 per process | 345 | 0.36 / 0.69 |
| Rig shared HTTP, one client | 365 (362–443) | 71 (66–114) | 0.19–0.38 | 107 | 0.67 / 1.72 |
| Rig shared HTTP, four clients total | 408 (364–482) | 83 (79–85) | 0.19–0.38 | 202 | 0.98 / 2.90 |
| Rig via `dotnet run --no-build` | 974 (922–1,130) | 265 (182–267) | 3.26–5.18 | 86 | 0.17 / 0.37 |
| Storybook docs-only 10.6.8 | 178 (174–204) | 68 (58–68) | <0.2 | 102 | 5.26 / 7.50 |
| Chrome MCP 1.9.0, no browser | 410 (403–461) | 123 (121–139) | <0.2 | unmeasured | unmeasured |

Four-client latency measures a complete parallel round of four calls, not one
individual request. Busy CPU is a short saturated burst, not sustained background
usage: 100 rig calls consumed 0.11–0.13 CPU seconds over 168–179 ms in the single
stdio case; four copies consumed 0.28–0.30 CPU seconds for 400 calls. Shared HTTP
consumed 0.31–0.36 CPU seconds for 400 calls. Storybook consumed 0.59–0.60 CPU seconds
for 100 full catalogue requests, using **136 real components** from the estate's
generated manifest. Storybook post-call RSS was **110–158 MiB**; idle-only figures
understate its working footprint. Rig HTTP with four clients was **88–94 MiB**
after calls; four stdio copies were **246–373 MiB**.

All spawned servers exited without remaining process-group children. Compiled
stdio closed on EOF in 55–65 ms; all four copies in 85–131 ms. Shared rig HTTP
stopped in 20–23 ms on SIGTERM. The companion's idle shutdown is also covered by
the behavior test, including an open SSE subscription.

The observed four-tab saving from shared HTTP was approximately **55–280 MiB idle
RSS**. A second always-on HTTP host still has a cost with no agents connected.
Reuse an already-needed dashboard; otherwise use compiled stdio for occasional
single-session work, or a manually started shared host for a multi-tab work period.
The SDK launcher has extra resident processes and early background work even with
`--no-build`; its five-second CPU window is not a steady-state idle claim. A separate
30-second sample averaged 0.99% CPU, with RSS falling from 196 to 100 MiB. An
exploratory compiled-stdio run earlier in the session started in 247–326 ms with
49–65 MiB idle RSS, illustrating the paging/cache variability.

The already-running OpenKnowledge service was observed separately: 23–110 MiB RSS
samples and 4–22% CPU across five-second windows with no benchmark tool calls.
Other clients/indexing were active, so this is **not isolated idle CPU**. Its
263–6,256 ms connection/discovery times are not cold server startup measurements;
the existing service was never restarted.

The [recorded samples](mcp-profile-2026-11-21.json) retain each run's startup,
RSS samples, CPU/wall times, call latency and shutdown results. Raw local reports
and validation logs are under `temp/mcp-profile/`. Rebenchmark on Windows/Linux
and after package/runtime changes before adopting defaults on those machines.

## Existing implementation

- `src/Rig.Web/Mcp/RigMcp.cs` registers eleven tools over the official C# MCP SDK. `RigOpsTools.cs` contains schemas and
  annotations; `RigOps.cs` supplies data.
- `rig web mcp` uses stdio. Each client connection starts its own process; stdin EOF shuts it down. Discovery does not
  launch a dashboard, browser or telemetry database. Telemetry is lazy; `trim_candidates` starts a background estate
  scan on demand.
- `rig web` already maps the same tools at `/api/mcp`. Clients can share this host. `rig tools install mcp` starts the
  **entire web host again**, on 8092, and installs automatic service startup/restart. It is not a smaller, MCP-only
  daemon.
- `rig tools install web` uses 8090. Use its existing `/api/mcp` endpoint when that dashboard is already needed; do not
  install a second host just for MCP.
- Rig has no built-in HTTP idle shutdown. Its installed long-lived services are supervised. Adding a timeout without
  changing supervision would cause restart loops.

See [web operations](web-ops.md) and [OpenKnowledge lifecycle](vault/knowledge-server.md).

## Reproduce the measurements

Read `node tools/mcp-profile/profile.mjs --help` first. It requires Node 22+ and macOS/Linux `ps`, with permission to
inspect processes and bind loopback sockets. It has no npm dependencies. Build separately, outside the measured
interval:

```bash
dotnet build rig.slnx -m:2
node tools/mcp-profile/profile.mjs --command '["dotnet","src/Rig.Cli/bin/Debug/net11.0/rig.dll","web","mcp"]' > temp/mcp-profile/rig-stdio.json
node tools/mcp-profile/profile.mjs --command '["dotnet","src/Rig.Cli/bin/Debug/net11.0/rig.dll","web","mcp"]' --copies 4 > temp/mcp-profile/rig-stdio-four.json
node tools/mcp-profile/profile.mjs --command '["dotnet","src/Rig.Cli/bin/Debug/net11.0/rig.dll","web","--port","18092","--no-open"]' --url http://127.0.0.1:18092/api/mcp > temp/mcp-profile/rig-http.json
node tools/mcp-profile/profile.mjs --command '["dotnet","src/Rig.Cli/bin/Debug/net11.0/rig.dll","web","--port","18092","--no-open"]' --url http://127.0.0.1:18092/api/mcp --clients 4 > temp/mcp-profile/rig-http-four.json
```

Create `temp/mcp-profile/` first. For an existing host use `--url ... --pid <pid>` instead of `--command`; the profiler
never stops that host. For stdio SDK launcher overhead, change the argv to
`dotnet run --no-build --project src/Rig.Cli -- web mcp`. Do not launch builds, tests or other benchmarks concurrently
with a measurement.

Each run starts fresh processes, completes `initialize`, sends `initialized`, lists tools, waits one second, samples
idle RSS for five seconds, and issues 100 sequential rounds of read-only calls. Rig rotates `disk_usage`, `list_jobs`,
`list_runs`, `decisions` and `attention`. Four-copy/client runs call all four in parallel within each round (400 calls
total). It then closes stdio or terminates its own HTTP host and checks for remaining process-group children. Timeouts
and failures clean up only the process groups the profiler started.

Startup is process-cold, with filesystem/package caches warm, not a reboot or empty package cache. `readyMs` includes
tool discovery. RSS is the sum of process-tree RSS in **MiB**, including launchers and live helpers; shared pages can be
counted more than once. It is neither allocated heap nor macOS physical footprint and excludes compressed/swapped pages.
macOS memory pressure can change RSS sharply between runs. CPU is delta cumulative process-tree CPU time divided by wall
time: **100% is one core**, and multicore work may exceed it. Exited sampled helpers retain their last CPU counter;
helpers that start and exit between samples can be missed. macOS `ps` has 10 ms CPU resolution, so a zero over five
seconds means below approximately 0.2% per sampled process, not proof of literally no CPU work. Short busy intervals
have coarse CPU percentages; use recorded CPU seconds and latency together.

These calls do not benchmark telemetry network probes, estate scans, destructive trim, starting jobs or browser
rendering. Benchmark those separately before adopting them as background workloads. `--tools ''` measures discovery/idle
only.

## Lifecycle choice

For occasional ops use in one project, launch the **compiled** stdio server and let EOF end it. Use the same stable
installed binary from every worktree. Avoid `dotnet run`/`npx` in session configuration: their build/package-manager
parents cost startup time and memory even when dependencies are cached.

For several tabs using ops concurrently, share the existing dashboard HTTP endpoint. If no dashboard is needed, a
manually started foreground web host can be shared for the work period and stopped afterwards. A permanent second
service spends RAM even when no agent needs it. There is no universal N threshold: compare measured total HTTP RSS with
N times measured stdio RSS and account for session memory. Direct HTTP connections require no local proxy process per
client.

To opt in with Claude, run `claude mcp add --help`, then use one of:

```bash
claude mcp add --scope project --transport http rig-ops http://127.0.0.1:8090/api/mcp
claude mcp add --scope project rig-ops -- dotnet /absolute/stable/install/rig.dll web mcp
```

Use `config/mcp/rig-http.json` as the equivalent JSON example. Merge just the wanted entry into the client's native
configuration; do not replace its other servers or credentials. Keep endpoints on loopback. Rig exposes job-start and
trim tools as well as reads; tool annotations are not authentication. A shared host uses its own working directory and
environment for every client, not each client's worktree. Validate one `tools/list` and read-only call before expanding
registration scope.

## Estate audit (21 November 2026)

This is a snapshot of readable local configuration, not a claim about every session or machine. No user settings,
project connector files outside rig, existing services or other agents' processes were changed.

| Location | Finding | Action |
|---|---|---|
| Rig source | stdio and shared HTTP already implemented; no listener on 8090/8092 | Keep startup opt-in |
| `~/.claude/settings.json` | `chrome-devtools-mcp@latest` stdio entry | Preserve existing; stop adding to new baselines |
| `~/.claude.json` | OpenKnowledge launcher falls back to `npx ...@latest mcp`; old shared-utilities project has Chrome MCP | Prefer existing shared knowledge endpoint; review stale project entry |
| Vault `.mcp.json` | Project-scoped OpenKnowledge stdio launcher | Overrides user HTTP; needs a vault-repo change |
| macOS LaunchAgents | Knowledge server has `KeepAlive`; package still under legacy data folder | Reuse listener; avoid a second install |
| Running processes | Shared knowledge server plus seven npm MCP parents and five explicit Node MCP children observed | Sessions must reconnect before a migration saves their RAM |
| `~/.codex/config.toml` | Node REPL configured; computer-use disabled; no rig/Storybook | No duplicate generic servers added |
| OpenCode `opencode.jsonc` | No MCP entry in parsed JSON | No global additions |
| Antigravity `mcp_config.json` | Empty file | No global additions |
| Personal/rtkelly-labs scan | Only vault `.mcp.json` outside ignored paths | Worktrees, other roots and client plugins excluded |
| Design-system main checkout | Storybook 10.5.5; components manifest enabled; no MCP addon | Docs host ready; dev addon needs incremental benchmark |

`rig vault knowledge-server connect` can preview migration of the Claude user entry, but its status/install paths use
the current rig data directory. On this machine the running service still points at the legacy directory; check the
existing healthy endpoint before installing anything. Project-scope vault config and already-running sessions remain
separate migration work. Do not kill other agents' stdio children to reclaim memory mid-session.

## Free companions

**Storybook:** the official [MIT MCP packages](https://github.com/storybookjs/mcp) provide an
[addon inside the dev server](https://storybook.js.org/addons/%40storybook/addon-mcp) and a
[standalone manifest-backed library](https://github.com/storybookjs/storybook/tree/next/code/lib/mcp). The audited
design-system checkout uses Storybook 10.5.5, enables `features.componentsManifest`, and has only
`@storybook/addon-docs` configured. Its dev command uses port **8100**, not the upstream example's 6006.

If Storybook is already open for UI work, add a compatible **exact** addon version in the design-system repo and share
`http://127.0.0.1:8100/mcp`. Measure the baseline dev server and the addon with the same workload before making this a
default. No addon was installed into that checkout by this task; another session was doing design-system work there.
Static Storybook hosting alone does not create `/mcp`.

For component knowledge without Vite or a browser, this repo supplies an optional small host over
**`@storybook/mcp@10.6.8` (MIT)**. It serves generated manifests from disk, rereads them on calls, binds only
`127.0.0.1`, and stops after 300 seconds without requests. Idle SSE subscriptions do not prevent shutdown; active
non-GET requests finish first. It is manually started; restart it before reconnecting a client after timeout. No
launchd/systemd service or auto-restarting supervisor:

```bash
npm ci --prefix tools/mcp-profile/storybook --ignore-scripts
node tools/mcp-profile/storybook.mjs --help
node tools/mcp-profile/storybook.mjs --module tools/mcp-profile/storybook/node_modules/@storybook/mcp/dist/index.js --manifests /absolute/path/to/storybook-static/manifests
claude mcp add --scope project --transport http design-system-docs http://127.0.0.1:18093/mcp
```

Build manifests in the design-system repo first using its existing build command.
The optional package has its own committed lockfile and is outside the root pnpm
workspace. Only the explicit install command fetches it. Its `node_modules` is
reclaimable; rerun `npm ci` when needed. All thirteen installed packages declare
MIT or ISC licenses. No paid API, cloud account, Docker, browser or indexer is required.
`config/mcp/storybook-http.json` is the matching opt-in example. This provides docs tools, not the addon's interactive
development/test tools.

```bash
node tools/mcp-profile/profile.mjs --command '["node","tools/mcp-profile/storybook.mjs","--module","tools/mcp-profile/storybook/node_modules/@storybook/mcp/dist/index.js","--manifests","/absolute/path/to/storybook-static/manifests"]' --url http://127.0.0.1:18093/mcp --tools docs-list > temp/mcp-profile/storybook.json
node --test tools/mcp-profile/profile.test.mjs
```

The published 10.6.8 package calls its tools `docs-list`, `docs-show` and `docs-show-story`; upstream main documentation
has used other names. Trust the pinned package's `tools/list`. Rebuild manifests when component docs change.

**Chrome DevTools MCP:** Apache-2.0; a browser adds substantial processes beyond the discovery-only baseline measured
here. Rig already uses `agent-browser` with its dedicated dev profile. Prefer that workflow unless a task specifically
needs Chrome MCP. Keep Chrome MCP project-scoped, pinned, and disconnected afterwards; never auto-connect daily Chrome
sessions. Existing baseline registrations remain until explicitly removed through the client's own configuration
mechanism.

**OpenKnowledge:** GPL-3.0-or-later; the estate already has a shared listener at `127.0.0.1:47311/mcp`. Share that
existing service rather than add more stdio copies. Its CPU can include indexing and other agents' requests, so
live-service sampling is observational, not an isolated idle or cold-start benchmark. Do not combine its idle timeout
with the current `KeepAlive` service. See the vault guide for connection commands and project-scope precedence.

Filesystem/git/shell MCP servers duplicate tools already supplied by coding agents and rig; no additional server is
justified here. No companion is enabled globally.

## Validation and limits

- Full C# solution suite: **6,358 passed, 45 skipped, zero failures** (6,403 total).
  The targeted Claude baseline class also passed all 25 tests.
- Node behavior tests: stdio initialize/list/call/EOF, HTTP manifest serving,
  foreign-origin rejection, and idle expiry despite an open SSE stream passed.
- Scoped C# formatting and `git diff --check` passed. The optional dependency
  lockfile was installed successfully with `npm ci --ignore-scripts`.
- Ownership gate: 361 inspected surfaces, zero violations. Scoped tooling and
  configuration filename lint passed, including after the optional package install.
- The ordinary `dotnet build rig.slnx -m:2` failed here because both the parent
  rig checkout and this nested checkout supply the same `.globalconfig` key.
  Roslyn unsets the generated-code exemption and raises RS0030 in DotMake output.
  Local binaries were built with `dotnet build rig.slnx -m:2 --disable-build-servers
  '-p:NoWarn=RS0030%3BNU1510'`; NU1510 is already suppressed by the repo normally.
  Tests then ran with `dotnet test --solution rig.slnx --no-build`. No analyzer
  policy was changed in source; a clean non-nested checkout should run the ordinary
  required build/test commands before landing.
- Full filename lint found generated Playwright `.sh`/`.ps1` files in nested `bin`
  directories. Scoped `-workdir tools/mcp-profile -config .ls-lint.yml` and
  `-workdir config/mcp -config .ls-lint.yml` runs passed before the optional install.

The profiler itself is portable across macOS/Linux, but these measurements cover
only this Mac. Process-cold startup, early idle and a read-only burst are measured;
filesystem-cold boot, browser rendering, the Storybook dev addon and isolated
OpenKnowledge indexing/startup are not. None of those unmeasured workloads was
enabled by default.
