# C# port: acceptance ledger

Tracking: [#85](https://github.com/rtkelly13/rig/issues/85), ADR 0022, and
[ownership.md](ownership.md). This ledger is the per-capability record the issue asks for: one row per
independently verifiable capability or platform, with the owner, the PR that landed it, what was
validated, and which native procedure is still outstanding.

It records evidence; it does not create any. A cell with no recorded source says **not recorded**
rather than filling the gap from memory. Merged source and green hosted CI are not native acceptance.

The ownership guide remains authoritative for registration and dispatch rules, the duplicate
baseline, and the named acceptance procedures. This ledger points at those procedures instead of
restating them.

At merge `71032fad` it carries fifteen rows with merged evidence and four rows that remain Python. No
row records a native acceptance run: every native cell is `pending` with its procedure, or
`not applicable` with its reason. #222 and #248 are internal work inside already switched surfaces,
so they update a row rather than add one.

## State vocabulary

One implementation state per row, describing the source tree at the recorded PR:

| State | Meaning |
| --- | --- |
| **not ported** | The implementation is still Python (or another runtime) and no C# owner exists. The remaining caller and removal condition stay visible in `ownership.md` |
| **implemented** | C# code exists but does not own a switched surface yet, or is internal plumbing behind a surface another row owns |
| **switched** | The named surface dispatches to C#. Named Python consumers may legitimately remain; they are listed in the callers or differences cell |
| **legacy deleted** | The switch also deleted the legacy implementation and tombstoned its registrations. The retired surface cannot come back without failing the ownership gate |

`switched` and `legacy deleted` are source facts. Neither implies native acceptance or rollout.

## Validation states

Native acceptance has one state per row, and only a recorded run can change it:

| State | Meaning |
| --- | --- |
| **pending** | No accepted native run is recorded. Name the procedure that would produce one |
| **passed** | A run under the named procedure is recorded with machine, artifact SHA, date, command and result |
| **failed** | A recorded run failed, or was invalidated by a later source change. The failure stays visible until re-run |
| **not applicable** | The capability has no native surface to accept (for example a retired command, or a report tool whose published-artifact smoke run is the whole check). Say why |

A hosted-runner pass, a skipped platform test or a successful compile is not `passed`.

## The ledger

| Capability / platform | Owner | PR | Implementation | Callers | Reference SHA / fixture | Intentional differences | Validation | Native acceptance | Deployed artifact |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `command-ownership` gate | C# `tools/command-ownership` over `config/command-ownership.json` | #211 | implemented | Required `ci` aggregate on every PR, including docs-only | merge `810ab0c9`; 460 inspected surfaces, ten-overlap baseline, zero violations | A registration and dispatch check, not semantic equivalence. Discovery fails closed and the baseline may only shrink | 9 C# discovery cases; Linux CI 4,636 passed / 25 skipped; gate green on the merged result | not applicable — CI-only gate with no native surface | Not changed by the slice |
| `chrome-tabs traces runs/show/summary/prune` (all platforms) | C# `Rig.Cli.Chrome.ChromeTabsCommand` | #211 | legacy deleted | Scheduled Python trace writer and daily retention; the `organise` run spans it reads | merge `810ab0c9`; existing span files are the input, no new fixture | Python report reader and CLI deleted (462 lines); writer and retention stay Python. The legacy spelling forwards the whole line | 8/8 published-artifact smoke with empty PATH and no Python checkout, destructive pruning only inside `temp/acceptance`; focused Python 112/112; parity in `ChromeTracesTests` and `ChromeTabsCommandTests` | not applicable — the published-artifact smoke is the whole check for a report tool | Not changed by the slice |
| Triage queue, CLI and web inbox | C# `Rig.Cli.ReviewQueue.TriageCommand`, `Rig.Web.TriageRoutes`, `Rig.Core.ReviewQueue` | #220 (CLI), #249 (web inbox) | switched | `rig triage refresh/verify`; web inbox; agent signals | merge `39d0eafb`; `tests/Rig.Core.Tests/ReviewQueue/*-oracle.json` | None recorded. Queue storage and service moved with paginated GitHub observations. #261 retires the remaining `src/rig/triage` modules and their tests: the browser tests seed the C# inbox through `tests/triage_seed.py`, and no Python triage module has a production caller | PR CI all applicable jobs passed; 67 core + 6 CLI `ReviewQueue` tests; ownership 454 surfaces, 10 pre-existing overlaps, 0 violations. Local full run 4,734 total / 4,679 passed / 25 skipped with 30 pre-existing `StepTimer` failures, reproduced on rerun | pending — `WEB-MCP` (inbox), `ROLLOUT` (installed CLI) | Not changed by the slice |
| Jobs installer, uninstall and scheduled entries | C# `Rig.Core.Jobs.JobsInstallTarget`, `JobsTaskXml`; `rigw` launcher | #209 | legacy deleted | Logon/Tick tasks; `rig jobs install/uninstall`; whole-line Python forwarding | merge `db468982`; `tests/Rig.Core.Tests/Jobs/task-xml-oracle.json`, `tests/Rig.Cli.Tests/Jobs/jobs-oracle.json` | Logon and Tick target the published `rigw` launcher. Python `jobs install` forwards help and unknown options unchanged. Display Guard and Python web consumers keep their implementations | C# 4,712 passed / 25 skipped; 97 focused Python, forwarding adapters 100% line/branch; coverage 95.50%; ownership 464 surfaces, ten unchanged overlaps, 0 violations; built `rigw jobs install --help` passed with empty PATH and no Python checkout | pending — `WIN-JOBS` | No real tasks installed or removed |
| Estate worktree removal | C# `Rig.Core.Estate.WorktreeRemovalJudge`, `GitWorktreePort` | #222 | switched | `estate tidy`, `estate reclaim`, `estate shelve` | merge `0bc72e96`; `tests/Rig.Core.Tests/Estate/SafetyMatrixTests.cs` is the oracle | Locked worktrees, ignored notes and reflog-only commits are deliberate refusals; `--force` still relaxes only the unpushed-commit check; unreadable inputs read conservatively | Full C# 4,810 total / 4,795 passed / 14 skipped / 1 pre-existing failure; SafetyMatrix + judge 91/91; estate/shelve CLI 91/91; coverage 95.30%; ownership 455 surfaces, 0 violations | pending — `ROLLOUT` (destructive runs on a disposable clone) | Not changed by the slice |
| Web MCP server | C# `Rig.Web.Mcp.RigOpsTools`; `rig web mcp` and `MapMcp("/api/mcp")` | #239 | legacy deleted | stdio MCP clients; HTTP MCP clients at `/api/mcp` | merge `bfb54beb`; the eleven tools are inspected from their `[McpServerTool]` attributes | The official `ModelContextProtocol.AspNetCore` transport replaces the Python server; both transports serve the same C# tools | PR CI all required checks passed; `tests/Rig.Web.Tests/McpTests.cs` and `tests/Rig.Cli.Tests/Web/WebMcpTests.cs`; exact counts not recorded | pending — `WEB-MCP` | Not changed by the slice |
| Web routes: triage, estate, telemetry | C# `Rig.Web.TriageRoutes`, `EstateOpsRoutes`, `TelemetryRoutes` | #249 | legacy deleted | Browser SPA and `/api/*` routes | merge `91e4a771` | None recorded beyond the port | PR CI all required checks passed; exact counts not recorded in the PR body | pending — `WEB-MCP` | Not changed by the slice |
| Estate connectivity graph | C# `Rig.Web.ConnectivityRoutes`, `Rig.Core.Connectivity.EstateGraph` | #207 | legacy deleted | `GET /api/estate/graph`, `GET /api/estate/graph/refresh`, SPA connectivity view | merge `0de2221e`; `tests/Rig.Core.Tests/Connectivity/graph-recorded.json` | The DuckDB ledger is gone: the join is in memory and the graph is reused from a 60 s snapshot in `temp/estate/connectivity.json`; `refresh` always rebuilds. Malformed input answers 503 | PR CI all required checks passed; parity against a Python-produced recorded graph, ported `EstateGraphTests`/`ManifestsTests`, `ConnectivityRoutesTests`; exact counts not recorded | pending — `WEB-MCP` | Not changed by the slice |
| macOS Chrome tab actions | C# `Rig.Core.Chrome.MacOS.ChromeMac` over `Rig.Cli.Chrome` | #240 | legacy deleted | `rig chrome-tabs list/rank/prune/group` on macOS | merge `c4d0734a`; `tests/Rig.Core.Tests/Chrome/MacOS/ChromeMacTests.cs` | Saved groups are Windows-only; macOS reports them unavailable and refuses deletion. `organise` stays Python | PR CI all required checks passed; `ChromeMacTests` run against fakes; exact counts not recorded | pending — `MAC-CHROME` | Not changed by the slice |
| Runtime install, rollback and status | C# `Rig.Core.Runtime.RuntimeManager`, `RigShim`, `Rig.Cli.Runtime.RuntimeCommand` | #241 | switched | `rig runtime install/rollback/status`; scheduled Python callers; legacy `desktop-manager runtime` forwarding | merge `5722e051`; `tests/Rig.Core.Tests/Runtime/`, `tests/Rig.Cli.Tests/Runtime/` | `rig_shim` was retired in favour of `RigShim`; `runtime.py` remains for the jobs and Chrome path/state consumers | Ownership guide runtime cutover: full build passed; 4,946 tests, 4,932 passed / 14 skipped; 25 focused Python forwarding cases; ownership 457 surfaces, 0 violations | pending — `ROLLOUT` | No runtime copy, shim or task installed or removed |
| C4 CLI | C# `Rig.Cli.C4.C4Command` over `Rig.Core.C4` | #242 | legacy deleted | `rig c4 check/model/tree/expand/derive dotnet`; the Python `rig.c4_okf` viewer route mounts its bundle reader | merge `65a5f95a`; C4 sample bundles and the analyzer/call-graph fixture | Byte-identical JSON, Mermaid and derived bundles to the replaced Python command. The web viewer stays Python under #78 | Ownership guide C4 cutover: full build zero warnings; 4,970 tests / 14 platform skips; byte-identical outputs; ownership 439 surfaces, 0 violations; `dotnet format --verify-no-changes` | not applicable for the CLI (byte-identical outputs recorded); the viewer stays pending under #78 and `WEB-MCP` | Not changed by the slice |
| Desktop workspace manager and display guard | C# `Rig.Cli.Desktop.DesktopWorkspaceCommand`, `Rig.Core.Desktop.DisplayGuard` | #250 | legacy deleted | `rig desktop workspace ...`; display guard scheduled entry | merge `678edc5f`; `tests/Rig.Core.Tests/Desktop/`, `tests/Rig.Cli.Tests/Desktop/DesktopCommandTests.cs` | `align` is C#; the guard runs through the rig launcher; Win32/COM/registry adapters are compile- and smoke-tested only on Windows | Ownership guide desktop workspace cutover: full Release build and solution test run passed; ownership 423 surfaces, 0 overlaps, 0 violations. Exact test counts not recorded | pending — `WIN-DESKTOP` | No installed task or desktop plan changed |
| Estate reconciliation pipeline | C# `Rig.Core.Reconciliation` (`IReconciler`, `ReconciliationRunner`) behind `Tidy`, `Reclaim`, `Trim` and `Shelver` | #248 | implemented | The four switched estate commands | merge `c9889a20`; `tests/Rig.Core.Tests/Reconciliation/`, `tests/Rig.Specs/Features/Reconciliation` | Shared plan/apply contracts with text and JSON output and distinct exit statuses; no command surface change | PR CI all required checks passed; exact counts not recorded in the PR body | pending — `ROLLOUT` (inherits the estate rows) | Not changed by the slice |
| Root tools: service lifecycle and database maintenance | C# `Rig.Cli.Tools.ToolsCommand`, `Rig.Core.Tools.ServiceManager`, `Rig.Core.Storage.RigDatabase`; `POST /api/db/backup` | #256 | legacy deleted | `rig tools install/ensure/uninstall/status/db ...`; legacy `rig web install/ensure/uninstall/backup/migrate/db-status` aliases; browser backup button | merge `71032fad`; `tests/Rig.Core.Tests/Tools/`, `tests/Rig.Cli.Tests/Tools/ToolsCommandTests.cs` | LaunchAgents on macOS, Task Scheduler tasks under `Shared Utilities\` on Windows, systemd user units on Linux. The remaining Python web child mounts only `JobsRoute` | `dotnet test rig.slnx -c Release` 5,594 total / 0 failed; coverage 95.15%; `uv run pytest` 1,896 passed / 17 skipped / 1 xfailed; `uv run status` PASS 10/10; ownership 398 surfaces, 0 violations | pending — `SERVICE-LIFECYCLE` (installed service), `WIN-NONINTERACTIVE`, `SHARED-LINUX` (build and tests), `ROLLOUT` | No service or database changed |
| `claude-sessions` retirement | None — retired without a C# port | #238 (issue #224) | legacy deleted | None; Orca and direct CLI/IDE workflows replace it | merge `e95b7b14`; the `src/desktop_manager/claude_sessions/` tree deleted | `work list` keeps its own transcript, live-session and ledger reader in `work/session_store.py` | PR CI all required checks passed; unit and ownership checks passed; exact counts not recorded | not applicable — no replacement surface | Not changed by the slice |
| `chrome-tabs organise` | C# `Rig.Core.Chrome.OrganiseRunner`; the surface stays Python-owned by `desktop_manager.chrome.cli:organise` and `desktop_manager.chrome.scheduled` | — | implemented | `rig chrome-tabs organise` fallback on all platforms; the `chrome-organise` scheduled job still runs `desktop_manager.chrome.scheduled` | `tests/Rig.Core.Tests/Chrome/OrganiseRunnerTests.cs` over the shared fakes; `tests/desktop_manager/test_chrome_organise.py` remains the whole-command oracle | The orchestration is C#: prune then group window by window, per-window selection restore, unattended hold/wait/retry/refusal, the run and phase trace attributes, and saved consolidation with its pending marker. The CLI registration, the Chrome trace-file writer wiring and the scheduled entry are not | 23/23 `OrganiseRunnerTests`; Chrome suites: Core 792/796 passed (4 skips), CLI 63/63; full solution 5,626 passed / 30 skipped / 0 failed | pending — `CHROME-ORGANISE` | Python implementation, unchanged |
| `work list` | C# `Rig.Core.WorkItems` core plus the still-Python `desktop_manager.work.cli:list_command` CLI until the cutover | — | implemented | `desktop-manager work list`; operator use only. Ledger row `py:desktop-manager work list` stays until the cutover; the C# core is not wired to a CLI yet | `tests/Rig.Core.Tests/Work/work-list.fixture.json`, generated by the Python implementation and compared byte-for-byte (PR #262; merge SHA pending) | Core only today: the Python CLI keeps the text view and session reader. Remaining: real Claude session source (`session_store` transcripts/registry/ledger/liveness/folder facts), text renderer and CLI options, CLI composition, behaviour tests, ownership switch and Python retirement. Since #224 `work list` owns its own transcript, live-session and ledger reader | 118 new `WorkItems` tests; 550 filtered `~Work` tests; 100% lines / 96.32% branches / 98.66% combined on the new namespace; `dotnet build rig.slnx -c Release -m:2` 0 warnings; `dotnet format --verify-no-changes` passed | pending — `ROLLOUT`; no native run is claimed | Not changed by the slice |
| `desktop help` | Python `desktop_manager.cli:help_cmd` | — | not ported | `desktop-manager help` | Ledger row `py:desktop-manager help` | A separate capability from `rig help`: it describes the desktop CLI | Python CLI tests | pending — `ROLLOUT` | Python implementation, unchanged |
| Python jobs registry | Python `desktop_manager.jobs.registry` (`Job`, `Trigger`, `default_jobs`) | — | not ported | Both composed job registries, the `python-job:` rows, the scheduled runner and the web coordinator | Ledger `python-job:*` rows; `tests/Rig.Cli.Tests/Jobs/jobs-oracle.json` is the C# installer's view of it | The C# installer is validated against the registry oracle; the registry itself stays Python until its own slice | Python jobs tests; #209's focused Python run passed 97 cases | pending — `WIN-JOBS`, `SHARED-LINUX` | Python registry, unchanged |

## How to record a row

1. **One row per capability/platform**, not per PR. A slice that ports several platforms may add
   several rows; a slice that only hardens an existing owner (like #222 or #248) updates that row's
   validation cell instead of adding a row.
2. **Owner** names the canonical type or module, not a file path that will move. Point at a directory
   only when the type is incidental.
3. **PR** is the merged PR, or `—` for a row that is not ported. An open PR is never recorded in this
   column; record it under "Open slices" instead. The implementation PR may still update the row's
   other cells before merge, as the update protocol below says; only this column waits for the merge.
4. **Reference SHA / fixture** is the squash merge commit and the oracle that makes the claim
   checkable — a recorded graph, a task-XML oracle, a golden bundle. `—` only when the validation
   itself is the whole record.
5. **Validation** copies the command class, result and skips from the PR body or the ownership
   guide's evidence section. If a count is not recorded, say "exact counts not recorded"; do not
   estimate, and do not substitute a later run's totals for the slice's own.
6. **Native acceptance** needs a procedure name, the machine, the artifact SHA, the date, the exact
   command and the observed result. Without all of that it stays `pending`.
7. **Deployed artifact** is what the slice changed on a real machine, or "not changed by the slice".
   Installing, removing or repointing a runtime, task, LaunchAgent or service needs its own
   `ROLLOUT` record.
8. Delete a row only when the capability is retired, and say so in the retirement PR. Retired rows
   leave a tombstone in `config/command-ownership.json`; history keeps the ledger row.

## Update protocol

- The slice's own PR updates its row and any row whose callers, differences or validation it changes.
  A row that cannot cite merged evidence waits for the merge.
- A PR that renames, adds or removes a named procedure in `ownership.md` updates every cell that
  refers to it in the same PR. Procedure names are stable identifiers, not prose.
- `state` changes only in the PR that switches or deletes the implementation. Docs-only PRs may
  correct a wrong citation, but never advance a state.
- Before #85 closes, every row is `switched` or `legacy deleted` — or retired with a stated reason —
  and every native cell is `passed` or `not applicable`. `ROLLOUT` stays open until the estate
  inventory for that capability is recorded.
- When this ledger and `ownership.md` disagree, `ownership.md`'s gate output wins and the ledger is
  corrected in the next PR.

## Open slices

These are reflected, not resolved. None of them changes a row above until it merges.

| Item | State | Effect on this ledger |
| --- | --- | --- |
| #257 jobs coordinator to `Rig.Web.JobsRoutes` | Open, green, blocked | The root-tools row's note that the remaining Python web child mounts only `JobsRoute`, and the Python jobs registry row, stay as they are until #257 merges |
| #258 Windows nightly regression | Open | Would supply recurring `WIN-NONINTERACTIVE` evidence; it is not an acceptance procedure by itself |
| #78 C4 member-level code views | Open | The `rig.c4_okf` viewer and its bundle reader stay Python; the C4 row's CLI acceptance does not cover them |
| #229 conceptual compression and idiomatic C# modernization | Open | Internal cleanup across ported subsystems; no row changes state and no acceptance is implied |
| #217 acceptance/caller gap | Recorded in `ownership.md` | The seven losing Windows Chrome registrations are gone while `WIN-CHROME` remains pending; the macOS Chrome row does not cover Windows |
| Organise orchestration core (#85) | Open | The `chrome-tabs organise` row moves to `implemented`; the surface, its CLI registration, trace writer and `chrome-organise` scheduled entry stay Python until they switch together |
| #262 `work list` C# core | Open; `implemented`, not switched | The `work list` row moves to `implemented`; its PR cell stays `—`, the CLI remains Python, and native acceptance stays pending until the cutover merges |
