# C# port: command ownership and retirement

Tracking: [#85](https://github.com/rtkelly13/rig/issues/85), ADR 0022, and
[#30 G5](../superpowers/plans/2026-10-29-rig-simplification-stack.md#pr-g5-one-cli-root).
This is the enforcement part of the port, alongside the platform acceptance proposal on #85.
Each port slice updates dispatch, removes its obsolete implementation and keeps the ownership gate aligned.
The completed trace-report retirement below records the source cutover separately from pending native acceptance.

## The inventory is inspected, then reviewed

[`config/command-ownership.json`](../../config/command-ownership.json) contains an exact registration
per surface and capability/platform. It is a review ledger, not the discovery mechanism.

```text
dotnet build rig.slnx -c Release
dotnet run --project tools/command-ownership -- --help
dotnet run --project tools/command-ownership --no-build -c Release -- . src/Rig.Cli/bin/Release/net10.0/rig.dll --inventory
dotnet run --project tools/command-ownership --no-build -c Release -- . src/Rig.Cli/bin/Release/net10.0/rig.dll --base origin/main
```

The inspector combines:

- The **production composition root**, traversing System.CommandLine objects after construction, including
  commands registered in loops, actions on groups, and aliases. The Windows Chrome registration branch is expanded
  with a driver that refuses native execution on this Mac. macOS gets its own composition export rather than
  inheriting the Linux tree; new native wiring must honor the inspection platform. Inspection invokes no actions.
  Pre-parser agent shortcuts, the bare launch, model shortcuts and actual external agent targets are exported too.
- Python AST discovery of Typer/Click factories, followed by **runtime traversal** of their composed command trees,
  including hidden commands and dynamically listed Click children. Unmounted apps are inspected too. Unsupported
  local factories, stateful callback closures, failed imports and unresolved children fail discovery.
  New console targets, GUI/plugin entry points and executable projects need explicit runtime adapters; a new ledger
  row cannot silently approve an unsupported framework. Named Python shims must resolve to their C# command.
- Every Python `__main__`/guard under core `src/`, console script target, development Python file, C# `Main`,
  top-level C# file app, C# command root and project (Roslyn syntax, project XML and evaluated MSBuild output types),
  C# root discovery includes target-typed `new()` and type aliases through Roslyn's semantic model.
  Node `bin`/package scripts and standalone Node scripts, tracked executable modes, and checked-in
  command/shell/PowerShell/LaunchAgent launchers. Launcher contents are fingerprinted.
- Each actual `Forwarded.Commands` route expanded into the inspected Python leaves. Probe arguments go back
  through **`Forwarded.Owns` itself**. A migrated action that still dispatches to Python fails discovery.
- Both composed job registries, including the optional workspace jobs, using inert fixture paths. They are
  callers, not independent implementations. No job is run. The MCP server's actual C# tool attributes and the Python web
  child's mounted route callables are inspected without serving requests. Legacy web callables are fingerprinted
  as opaque dispatch units; this is not proof of individual HTTP endpoint parity.

The Python probe is a development inspection adapter for the legacy runtime, not new rig product behavior.
The ownership rules, source inspection and gate runner are C#. Remove the probe with the last core Python routes.

`Id` identifies the observed registration. `Capability` is its normalized command path; `Owner` is the canonical
implementation. A Python wrapper counts as an alias only when its inspected AST is exactly a whole-line dispatch
to the real `csharp_cli.run`, without branches, fallback or independent behavior. Only Typer's exact built-in
callback adapter is stripped; user decorators are independent behavior. Reusing one callback under
several names also shares one implementation. C# aliases come from the actual command object's alias collection.
New registrations need reviewed ledger rows. Metadata must match discovery, so changing a ledger owner or platform
cannot conceal an independent implementation.

### Current owners and callers

| Capability/surface | Canonical implementation and callers | Source state / remaining condition |
| --- | --- | --- |
| C# registrations (`cs:` rows) | `rig`, plus 13 Python console-script compatibility spellings | Switched in source; shared tests/CI evidence does not establish native acceptance or deployment |
| `desktop workspace` | C# `rig desktop workspace` (restore, audit, status, validation, features, export, wire, align, name-desktops, guard and the per-entry commands) | CLI and native adapters switched in #243; the Python Typer workspace group and its `cli_align`/`cli_apps`/`cli_machine`/`align`/`display_guard_windows` modules retired. `align` is C#. Windows desktop acceptance remains the `WIN-DESKTOP` procedure |
| `runtime`, `work` | Inspected Python leaves in the ledger | Not ported unless separately recorded; keep their exact caller and removal condition visible during each cutover |
| `triage refresh/verify` | C# `Rig.Cli.ReviewQueue.TriageCommand`, `Rig.Web.TriageRoutes` and `Rig.Core.ReviewQueue.AgentSignals` | CLI, web inbox and agent-signal consumers switched; Python CLI, six platform fallback routes, `rig.triage.web` and `rig.triage.agents` retired. The remaining `src/rig/triage` models, store, service, GitHub and Markdown modules and their tests are retired too; the browser tests seed the C# inbox through `tests/triage_seed.py`, and no Python triage module has a production caller |
| `c4` | C# `Rig.Cli.C4.C4Command` over `Rig.Core.C4` | CLI, checks, composition, derivation and the Roslyn call-graph reader switched; the Python CLI, derive scanner and 21 registrations retired. The `rig.c4_okf` web viewer and the bundle reader it mounts stay explicitly owned until that route is switched |
| `jobs` | C# status/last/log/run/install/uninstall | All legacy spellings dispatch whole-line to C#; #209 owns runtime progression and Windows acceptance |
| `runtime` | C# status/install/rollback; Python `runtime.py` still serves paths and state to scheduled Python callers | Legacy spellings dispatch whole-line to C#; `runtime_cli` execution and `rig_shim` retired. `runtime.py` stays for jobs/chrome path and state consumers; #227 owns native acceptance |
| `web`, `tools` | C# public host, C# MCP server (`/api/mcp` and stdio), and C# service/database lifecycle (`rig tools`); the legacy `rig web` service and database subcommands are aliases | Triage, estate, telemetry and database-backup routes are C# (`Rig.Web.TriageRoutes`, `EstateOpsRoutes`, `TelemetryRoutes`, `HostRoutes`); `Python service_installer` and `rig.db` retired with their tests; the jobs coordinator, C4 and the estate graph stay in the Python child (`desktop_manager.web_upstream`) |
| `web mcp` | C# `Rig.Web.Mcp.RigOpsTools` over `Rig.Core`, served by `rig web mcp` (stdio) and `app.MapMcp("/api/mcp")` | `desktop_manager.web_mcp` and its Python CLI command are retired; the eleven tools are inspected from their `[McpServerTool]` attributes |
| `chrome-tabs` | C# traces everywhere; C# native `list`, `rank`, `prune` and `group` on Windows and macOS; `saved-groups`, `install` and `uninstall` on Windows; C# `organise` orchestration core; Python `organise` | macOS `group`, `list`, `prune` and `rank` registrations retired in #226 with the native `ChromeMac` driver. `Rig.Core.Chrome.OrganiseRunner` now carries the orchestration and is covered by fakes, but the surface, its CLI registration, its trace writer and the `chrome-organise` entry still dispatch to Python until that scheduled slice moves |
| `chrome-tabs traces runs/show/summary/prune` | C# only; legacy spelling dispatches the whole line | Legacy report CLI and 462 lines of reader/report code deleted; writer/daily retention still have scheduled Python callers |
| `work` | C# `Rig.Cli.Work.WorkCommand` over `Rig.Core.WorkItems` | `rig work list` is `legacy deleted` in the acceptance ledger (#276): the Python package, its tests and its `desktop-manager work` registration are gone and tombstoned |
| `claude-sessions` | Retired without a C# port (#224) | Eight Python leaves and fourteen modules deleted; the `work` list keeps its own transcript, live-session and ledger reader in `work/session_store.py`. Orca and direct CLI/IDE workflows replaced the window-focused orchestration |
| Core Python modules, development file apps, Node scripts and launchers | `module:`, `python-file:`, `main:`, `file-app:`, `node:`, `launcher:` rows | Development tooling is inventoried separately from product CLI behavior |
| Web/MCP and jobs | `web-child:`, `mcp:`, `python-job:`, `cs-job:` rows | The `mcp:` tool set is now C#; `desktop_manager.web_mcp`, the triage, estate and telemetry web children, and the jobs coordinator are retired. No `web-child:` rows remain, and job callers remain Python where named until their slices switch them |
| Brand pipeline | `tools/image-drift` | Intentional Python exception, its own project/gates (ADR 0022) |

There are 14 current console scripts, rather than the issue's original 15. The ledger follows actual registrations.
`desktop-manager help` describes the desktop CLI, so it is a separate capability from `rig help`.
`rig desktop workspace` controls desktop plans; `rig workspace` manages development configuration.
The legacy `workspace align` spelling maps to the `align` capability, and `ensure-desktops`/`sync-desktops`
map to `name-desktops`, so those alternate spellings cannot conceal an independent ported implementation.

## Refusal rules

One independent implementation owns each leaf capability on each supported platform. Partial ports must be
disjoint; a spelling alias delegates to the same implementation. New unregistered executables, duplicate owners,
unresolvable aliases, stale forwarding and retired source/entries fail the gate. Tombstones are permanent.

The initial baseline held exactly **ten** capability/platform overlaps: seven Windows Chrome commands
(`group`, `install`, `list`, `prune`, `rank`, `saved-groups`, `uninstall`) and the web-launch callback on Linux,
macOS and Windows. C# was the desired canonical owner for these rows. #217 retired the losing Python
registrations — the Windows-only `install`, `saved-groups` and `uninstall` commands, the Windows platform
declaration on the macOS `group`/`list`/`prune`/`rank` commands, and made the bare `desktop-manager web`
a verified thin dispatcher to `cs:web` — so the baseline is now empty. A Mac Chrome cutover must remove its
obsolete Python registrations in the same PR rather than add macOS baseline allowances.

The jobs progression (#85, #30 G5) adds `rigw`, a windowless entry point into the same production CLI.
Its discovery adapter verifies that the launcher's only member passes the original argument array directly to
`Cli.Program.Main`; extra behavior or rewritten arguments fail discovery. Its executable project and entry point
are registered separately without introducing another command owner. `jobs uninstall` now has one C# owner;
the Python spelling delegates help and unknown options unchanged through `desktop_manager.jobs.forward`.
The old Python callback and its three fallback routes are tombstoned. Installer/task XML now run in C#; Logon and Tick target `rigw` directly. The legacy scheduled
module delegates to that published launcher, while Display Guard and Python web consumers retain
their existing implementations. `WIN-JOBS` acceptance and rollout remain pending separate evidence.

Each C# invocation records a root Activity with `cli.argv` (the original argument array) and `cli.cwd` at start,
including help, parsing errors, agents and forwarded commands. Process spans record `process.command_args` and
the effective `process.cwd` at start on both sync and async paths. The `rig` trace writer records these in the
usual environment-controlled trace directory; the command-tree inspection path does not start it.

This narrow temporary baseline was explicitly authorized for #85; it does not relax other architecture gates.
CI compares it with the PR base: entries can only disappear, never gain an owner, capability or platform. Resolved
baseline entries must be deleted, so an allowance cannot linger and permit a later resurrection. New retired entries
and deleted source paths are added as slices land. There is no refresh/accept-baseline command in CI.

`command-ownership` runs even for docs-only PRs, on GitHub's **merged result**, and feeds the required aggregate
`ci` check. It also verifies current main is an ancestor of that result. Main's inspected ruleset requires strict
up-to-date `ci` checks with no bypass actors. A concurrent merge therefore requires a fresh result; a stale branch's
green ownership check is insufficient. The workflow does not grant write permissions or change branch protection.

This proves **command registration and dispatch ownership**, not arbitrary semantic equivalence. New names can
hide equivalent business code; reviewers must assess capability naming and adapters. Source reflection cannot
prove future arbitrary plugin frameworks complete. A new framework, extra composition root or uninspectable
registration needs a discovery adapter and regression test; never suppress discovery failures. Installed machines
and external repositories are a separate rollout inventory, not inferred from CI's source tree.

## Evidence and native acceptance

The initial ledger contains **460 inspected surfaces**. Local validation on macOS at the PR's source state:
the full C# solution run passed 4,625 tests with 25 explicit skips; the final CLI rerun passed 918/918 and the
final discovery rerun passed 9/9. Combined line/branch coverage holds the unchanged **95%** floor with
only CI's existing generated-interop filters (exact final measurement on the PR). The focused Python suite passed
112/112, including all organise trace-consumer assertions after removing their dependency on the retired reader.
The published trace artifact
passed eight smoke cases with an empty PATH and an unavailable Python checkout, including destructive pruning
only inside `temp/acceptance`. Strict types, import contracts, the ratchet, lint/format, security, workflow audit
and documentation audit passed. Windows CI and interactive procedures remain separately recorded on #85.

The trace slice preserves the C# `ChromeTracesTests` parity assertions and `ChromeTabsCommandTests` argument,
JSON, ordering, errors, exit codes and retention cases. The Python tests retain writer/exporter/daily-retention
behavior and exercise whole-line alias dispatch, including unknown arguments and help. The ownership suites test
duplicate/unregistered/retired refusals, disjoint ports, thin aliases, baseline expansion, dynamic C# registrations,
new executable sources, unsupported discovery and stale fallback. No coverage exclusions or lowered gates are added.

Record passing commands, commit, coverage and skips in the PR and #85. A built/published trace-report smoke run
must use a fixture directory and an empty PATH with Python forwarding unavailable. No interactive desktop is
required for reports; native actions are outside this slice. Compilation and skipped native tests are not acceptance.

Read-only local rollout observation on 2026-11-13: `~/.local/bin/rig` resolves to the native .NET tool at
`~/.dotnet/tools/rig`. There is no `desktop-manager` or `chrome-tabs` on this shell's PATH. The two rig LaunchAgents
are `dev.rtkelly.rig.open-knowledge` (Node/open-knowledge) and `dev.rtkelly.rig.otelcol` (native collector);
their plist arguments do not launch core Python. This does not prove all estate configs or other machines migrated.
No installed tool, LaunchAgent or scheduled task was modified for this PR.

Jobs progression validation on macOS, 2026-11-14: `dotnet build rig.slnx -m:2 --no-restore` passed with
no warnings; `dotnet test --solution rig.slnx --no-build --coverlet --coverlet-output-format json` with the
existing generated-interop filters passed 4,689 tests with 25 explicit skips. The five fresh reports measured
95.58% combined line/branch coverage against the unchanged 95% floor. The ownership gate against `origin/main`
passed with 461 inspected surfaces, ten unchanged overlaps and zero violations. Focused Python forwarding tests,
Ruff, both type checkers, import contracts and the ratchet passed. Built `rig` and `rigw` uninstall help produced
root traces; `rigw` also passed with an empty PATH and an unavailable Python checkout. These checks did not
install or remove any real tasks and do not supply `WIN-JOBS` native acceptance.

C4 cutover validation on macOS, 2026-11-14: the full C# build passed with zero warnings. The full solution
test run passed 4,970 tests with 14 existing platform skips. `rig c4 check|model|tree|expand` and
`rig c4 derive dotnet` produced byte-identical JSON, Mermaid and derived bundle files to the Python command
they replaced, including the sample bundles and the analyzer/call-graph fixture. Ruff, both Python type
checkers, import contracts, the ratchet and `dotnet format --verify-no-changes` passed. The ownership gate
against `origin/main` passed with 439 inspected surfaces, no overlaps and zero violations. The Python
viewer routes remain owned and tested; no installed runtime, task or LaunchAgent was changed.

Installer and scheduled-entry cutover validation on macOS, 2026-11-14: the full C# build passed with
zero warnings and errors. The full solution coverage run passed 4,712 tests with 25 existing skips and
zero failures. Five fresh reports in `temp/jobs-coverage` measured **95.50% combined line/branch
coverage**, passing `scripts/coverage-floor.cs` against the unchanged 95% floor and `origin/main`'s
props. The ownership gate against `origin/main` passed with 464 inspected surfaces, ten unchanged
overlaps and zero violations. Focused Python checks passed 73 tests plus 24 web tests; the forwarding
and scheduled-entry adapters measured 100% line/branch coverage. Ruff, strict types, import contracts,
the ratchet and C# whitespace checks passed. Built `rigw jobs install --help` also passed with an empty
PATH and an unavailable Python checkout. The task XML oracle covers all three tasks, preserving
interactive identity, timing and restart policy while changing Logon/Tick to the native launcher.
No real tasks were installed or removed; `WIN-JOBS` and rollout remain pending.

Runtime cutover validation on macOS, 2026-11-14: `dotnet build rig.slnx -m:2` passed with zero
warnings and errors, and `dotnet test rig.slnx -c Release` ran 4,946 tests: 4,932 passed with 14 existing
skips and zero failures. The ownership gate against `origin/main` passed with 457 inspected surfaces, zero
pre-existing overlaps and zero violations. Focused Python runtime and forwarding tests passed 25 cases;
Ruff passed on the new forwarders. The Python `rig_shim` module and its test were retired in favour of
`RigShim`, and the three legacy runtime spellings dispatch whole-line to C#. `runtime.py` remains for the
scheduled Python callers that still use its path and state helpers. No real runtime copy, shim or
scheduled task was installed or removed for this source cutover, so native runtime installation remains
a rollout check rather than evidence recorded here.

Triage retirement validation on macOS, 2026-11-16: the full C# Release build passed with zero warnings and
errors. `Rig.Web.Tests` passed 120/120 and `Rig.CommandOwnership.Tests` 10/10. The ownership gate against
`origin/main` passed with 398 inspected surfaces, zero overlaps and zero violations. The remaining `src/rig/triage`
modules and their GitHub/Markdown/queue tests are deleted; the browser suite passed 21/21 against the C# host with
its seed builders moved to `tests/triage_seed.py`. Ruff, basedpyright, `ty`, import contracts and the ratchet
passed. No installed runtime, task or LaunchAgent was changed.

Desktop workspace cutover validation on macOS, 2026-11-15: the full C# Release build and solution test run
passed, and the ownership gate against `origin/main` passed with **423 inspected surfaces**, zero overlaps and
zero violations. The Python Typer `workspace` group, its five command modules and the align-port adapter were
deleted; `rig desktop workspace` owns restore, audit, status, validation, features, export, machine wiring,
align, desktop naming, the display guard and the per-entry commands. Core tests cover the reconciler, the
guard's state machine, align decisions and plan loading; the native Win32/COM/registry adapters are compile-
and smoke-tested only on Windows. `WIN-DESKTOP` remains the interactive acceptance procedure.

Organise orchestration core validation on macOS, 2026-11-16: `dotnet build rig.slnx -c Release`
passed with zero warnings and errors. `OrganiseRunnerTests` passed 23/23 over the shared fakes; the
Chrome suites passed 792/796 (4 platform skips) in Core and 63/63 in the CLI. The full solution run
passed 5,626 of 5,656 with 30 skips and zero failures. The ownership gate against `origin/main` passed with
398 inspected surfaces, zero pre-existing overlaps and zero violations. `dotnet format
--verify-no-changes` was clean, and `uv run status` passed every check except the expected dirty
worktree. The Python `test_chrome_organise.py` oracle passed 19/19, so the surface is unchanged; the
orchestration core is `implemented`, not switched.

Named remaining acceptance procedures, all run from a reviewed/published artifact in a disposable environment:

| Procedure | Runnable entry and required environment | Evidence/expected result |
| --- | --- | --- |
| `WIN-DESKTOP` | Interactive Windows, disposable plan/windows: `rig desktop workspace validate-plan --config <plan>` then `rig desktop workspace restore --config <plan> --dry-run`, then an explicitly scoped restore/audit/align | Record Windows build, permissions, before/after layout, exact command, logs and restored state. Real Win32/UIA placement is pending, not a hosted-runner pass |
| `WIN-JOBS` | #209's reviewed runtime in a disposable Windows account: `rig jobs install --help`, `rig jobs status`, its named task-install/logon acceptance | Record task XML, target artifact, due/skip behavior, background console behavior and uninstall/cleanup; no production task changes merely to test source |
| `WIN-CHROME` | Interactive Windows, dedicated dev Chrome profile: `rig chrome-tabs list --profile <dev> --json`; `rig chrome-tabs prune --profile <dev> --dry-run`; controlled prune/group/saved-groups on disposable tabs | Record actual UI effects, focus restoration and cleanup. #217 removed the seven losing Python registrations while this acceptance is still pending; see the acceptance/caller gap note below |
| `MAC-CHROME` | #226 artifact (`ChromeMac`), repository dev browser profile: `rig chrome-tabs list --profile <dev> --json`, controlled prune/group with disposable windows/tabs | Record Accessibility/Automation grants, native calls, actual changes, cleanup and restored focus. No daily Chrome profile |
| `CHROME-ORGANISE` | macOS and Windows, dedicated dev Chrome profile, disposable windows/tabs: `rig chrome-tabs organise --profile <dev> --dry-run`, then a controlled `rig chrome-tabs organise --profile <dev> --unattended --wait-minutes <n>` run, then `rig chrome-tabs traces runs` and `traces show <run_id>` for the run's spans | Record the dry-run plan, the actual prune/group moves, the trace run and span output, that no other profile was touched, the refusal path (live call, locked session or a key press leaving grouping unfinished), restored virtual desktop and focus, that the `chrome-organise` scheduled entry invokes the same command (`rig jobs status`, `rig jobs last chrome-organise`), and cleanup. No daily Chrome profile |
| `SHARED-LINUX` | A Linux host or container without a Windows or macOS desktop session, reviewed artifact: `dotnet build rig.slnx -c Release`, `dotnet test --solution rig.slnx -c Release`, then the shared capability's own `--help`, `--json` or fixture run | Record distro and kernel, .NET SDK/runtime versions, artifact SHA, exact commands and test totals/skips. Proves the third platform's shared core and non-interactive CLI; it does not accept Win32/UIA, macOS Chrome or an installed service |
| `WIN-NONINTERACTIVE` | A Windows host in a non-interactive session, reviewed artifact: `dotnet build rig.slnx -c Release`, `dotnet test --solution rig.slnx -c Release`, then each Windows-relevant command's `--help` and `--json` path | Record Windows version, .NET versions, artifact SHA, test totals/skips, command output and exit codes. Hosted CI or a label-triggered run never substitutes; interactive UI effects stay with `WIN-DESKTOP`, `WIN-JOBS` and `WIN-CHROME` |
| `WEB-MCP` | Disposable DB/ports, no production service: `rig web --port <free> --no-open`; `rig web mcp` over stdio | Browser → route → data → response and real initialize/tools exchange; the MCP server is C# on both transports, so the check is that each remaining Python child is still identified and protocol stdout stays clean |
| `SERVICE-LIFECYCLE` | A disposable macOS, Windows or Linux host with no production service, reviewed artifact: `rig tools install <target> --preview`, then the real install, `rig tools ensure <target>`, `rig tools status <target>`, `rig tools uninstall <target> --preview`, then the real uninstall | Record the artifact SHA, the installed launchd plist/Task Scheduler task/systemd user unit, the PID and listener before and after `ensure`, the `status` output and exit codes, and that uninstall leaves no unit, plist, task, runtime copy or orphan process. Repeat per target and platform; a compile or `--help` run is not lifecycle evidence |
| `ROLLOUT` | After platform acceptance: inspect runtime/PATH/task/LaunchAgent/agent-config/estate-repo callers; use the documented installer for that slice | Record machine and artifact SHA. Keep the last working deployed runtime until replacement acceptance. Source deletion alone never establishes rollout |

Windows CI can be triggered from this Mac by the `ci:windows` label. It supplies compilation/noninteractive
evidence only, recorded under `WIN-NONINTERACTIVE`. These procedures remain pending until a native run is
recorded. Do not close #85 on this PR.

**#217 acceptance/caller gaps.** Removing the seven losing Windows Chrome registrations is a source-ownership
change only. No native `WIN-CHROME` run is recorded from this Mac, so `rig chrome-tabs
group|list|prune|rank|saved-groups|install|uninstall` is now the sole Windows path while the real UI
effects, focus restoration and cleanup remain to be accepted. Any caller still spelling
`desktop-manager chrome-tabs ...` on Windows must migrate to the `rig` spelling; this PR updates the
repository's own callers and the ledger, not installed runtimes, tasks or other machines. The `WIN-CHROME`
and `ROLLOUT` rows above stay open until that evidence is recorded.

## Sunset the migration gate at final cutover

_State after #217:_ the overlap baseline is empty, but this is an overlap-only slice, not port completion:
main still has core Python files and dozens of Python-owned registrations, and routes still have callers
(`jobs install` and the remaining web children). The ledger and this
gate stay in place until the source migration, native acceptance and rollout criteria above are met.

This cross-language inventory is temporary migration machinery. Close it down in the final #85 cutover PR
once all core commands, web/MCP children and job callers have C# owners, the overlap baseline is empty,
core Python implementations and forwarding fallbacks are deleted, and the named native acceptance and estate
rollout evidence above is recorded. ADR 0022's isolated `tools/image-drift` exception remains separate.
An empty discovery result alone is never sufficient: failed or unsupported discovery must continue to fail closed.

That final PR should delete the Python inspection adapter and its tests, the migration ledger and baseline,
temporary legacy capability mappings, unused discovery tooling/dependencies, and the `command-ownership` CI job
and aggregate dependency. Preserve the migration evidence in #85 and Git history rather than maintaining a
per-command migration roster forever. Source deletion and deployed runtime rollout remain separate checklist items.

In the same PR, replace this machinery with small permanent C# architecture and command-tree checks for unique
canonical registrations, thin aliases, one CLI front door, and rejection of core Python or legacy forwarding
returning. These keep retirement enforced after the per-entry tombstones are removed. Keep the existing coverage,
nullable, analyzer and architecture gates. Do not add an automatic disable flag while the port is incomplete;
review the final removal against recorded source, native acceptance and rollout evidence before closing #85.
